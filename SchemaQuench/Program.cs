// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Microsoft.Extensions.Configuration;
using Schema.Checkpointing;
using Schema.Domain;
using Schema.Isolators;
using Schema.Configuration;
using Schema.Utility;
using Schema.Validation;

namespace SchemaQuench;

public static class Program
{
    public static void Main(string[] args)
    {
        CommandLineParser.HandleCommonSwitches("SchemaQuench", ToolSpecificSwitches);

        var skipKindlingForge = args.Length > 0 && args[0] == "SkipKindlingForge";
        AppDomain.CurrentDomain.UnhandledException += UnhandledException;
        LogFactory.LogInitializer = ConfigHelper.ConfigureLog4Net;
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("SchemaQuench", LogFactory.GetLogger("ProgressLog").Info);
        try
        {
            CommandLineParser.RefuseUnrecognizedArguments(KnownArguments);
        }
        catch (RunFailedException e)
        {
            LogBackup.FailedRunExit("SchemaQuench", e);
            return;
        }
        SettingsContract.WarnOnUnrecognizedKeys(config, SettingsTool.SchemaQuench, LogFactory.GetLogger("ProgressLog").Warn);

        RegisterCheckpointing();

        // Checked before ProductQuench is constructed: ProductQuench's constructor eagerly calls
        // Product.Load(), which throws on a malformed package — that would crash Main before this
        // gate ever ran. --Validate does its own loading via PackageLoader so a load failure is
        // reported as a finding (SS-LOAD-001) instead of an unhandled exception.
        if (CommandLineParser.ContainsSwitch("Validate"))
        {
            var validator = new SchemaPackageValidator(PackageLoader.LoadPackage, ValidationCheckRegistry.Default());
            var result = validator.Validate(config[SettingsKeys.SchemaPackagePath] ?? ".");
            foreach (var line in ValidationReporter.Render(result.Findings))
                LogFactory.GetLogger("ProgressLog").Info(line);
            LogBackup.BackupLogsAndExit("SchemaQuench", result.HasErrors ? 2 : 0);
            return;
        }

        ProductQuench productQuench;
        try
        {
            productQuench = new ProductQuench();
        }
        catch (RunFailedException e)
        {
            LogBackup.FailedRunExit("SchemaQuench", e);
            return;
        }

        var testConnection = CommandLineParser.ContainsSwitch("TestConnection");
        var previewTargets = CommandLineParser.ContainsSwitch("PreviewTargets");
        if (testConnection || previewTargets)
        {
            bool ok;
            try
            {
                ok = productQuench.RunPreFlight(previewTargets);
            }
            catch (RunFailedException e)
            {
                LogBackup.FailedRunExit("SchemaQuench", e);
                return;
            }
            LogBackup.BackupLogsAndExit("SchemaQuench", ok ? 0 : 2);
            return;
        }

        // --ResumeQuench is opt-in (#332): without it, discard any leftover checkpoint so a
        // re-run starts fresh and is never influenced by a stale checkpoint. Checkpointing
        // stays active for this run — a fresh failure still writes a checkpoint for a later
        // --ResumeQuench. Uses the same FileCheckpointManager (and directory) the run will use.
        if (!CommandLineParser.ContainsSwitch("ResumeQuench"))
            CleanupCheckpoints();

        try
        {
            productQuench.QuenchProduct(skipKindlingForge);
        }
        catch (Exception e) when (ConnectionLostClassifier.IsConnectionLost(e))
        {
            // Last-resort net for a mid-deploy drop that escapes the per-server/per-database seams
            // (which name the specific server): surface the disconnect message instead of a raw
            // AppDomain stack. The failing server is not reliably known here, so the message stays
            // generic ("the target server") rather than falsely asserting the primary. Non-connection
            // exceptions still reach the AppDomain UnhandledException handler unchanged.
            LogFactory.GetLogger("ProgressLog").Error(ConnectionLostMessage.Build(null, "deployment"));
            LogFactory.GetLogger("ErrorLog").Error("Lost connection during deployment", e);
            LogBackup.BackupLogsAndExit("SchemaQuench", 2);
            return;
        }
        catch (RunFailedException e)
        {
            // A failed validation, product script, version check or refused target: the run is over, the user can fix
            // it, and checkpoints stay for --ResumeQuench.
            LogBackup.FailedRunExit("SchemaQuench", e);
            return;
        }

        // Clean up checkpoint files only on a clean success — a failed run must preserve
        // checkpoints so the next invocation can resume.
        if (!productQuench.Failed)
        {
            CleanupCheckpoints();
            // 1 when a feature was downgraded on a target that lacks it: the deploy finished, but not everything the
            // package declares is in place. The summary lists each downgrade.
            LogBackup.BackupLogsAndExit("SchemaQuench", productQuench.Incomplete ? 1 : 0);
        }
        else
        {
            // Continue mode: some work units failed but the run was not aborted mid-template.
            // Exit with code 2 to signal partial failure to the caller.
            LogBackup.BackupLogsAndExit("SchemaQuench", 2);
        }
    }

    public static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogBackup.UnhandledExceptionLogger("SchemaQuench", e);
    }

    /// <summary>
    /// Registers an ICheckpointing implementation in FactoryContainer using
    /// --CheckpointDirectory (or the CheckpointDirectory config key) when provided.
    /// Without either, ProductQuench falls back to FileCheckpointManager.GetFromFactory()
    /// which uses a default temp directory.
    /// </summary>
    private static void RegisterCheckpointing()
    {
        var dir = CommandLineParser.ValueOfSwitch("CheckpointDirectory");
        if (string.IsNullOrWhiteSpace(dir))
            dir = FactoryContainer.Resolve<IConfigurationRoot>()?[SettingsKeys.CheckpointDirectory];
        if (!string.IsNullOrWhiteSpace(dir))
            FactoryContainer.Register<ICheckpointing>(new FileCheckpointManager(dir));
    }

    private static void CleanupCheckpoints()
    {
        try
        {
            FileCheckpointManager.GetFromFactory().DeleteCheckpoints(Product.Load().Name);
        }
        catch
        {
            // Product failed to load or no checkpointing registered — nothing to clean.
        }
    }

    private static readonly string[] KnownArguments =
    {
        "SkipKindlingForge", "Validate", "TestConnection", "PreviewTargets", "ResumeQuench", "ForceReKindle", "WhatIf"
    };

    private static void ToolSpecificSwitches()
    {
        Console.WriteLine("  --Validate                       Statically validate the schema package (no database connection), then exit.");
        Console.WriteLine("  --TestConnection                 Validate server connection(s) + minimum version, then exit. No deployment.");
        Console.WriteLine("  --PreviewTargets                 Validate, then list the databases/schemas each template would target (read-only). No deployment.");
        Console.WriteLine("  --ResumeQuench                   Resume from an existing checkpoint if one is present.");
        Console.WriteLine("  --CheckpointDirectory:<path>     Directory for checkpoint files (default: %TEMP%/schemaquench-checkpoints).");
        Console.WriteLine("  --ForceReKindle                  Re-deploy the SchemaSmith helper procedures this run even if the in-database kindle stamp is current.");
        Console.WriteLine("  --WhatIf                         Dry run: report what would change and change nothing of yours (same as the WhatIfONLY setting).");
        Console.WriteLine("  --WhatIfDetail:<mode>            WhatIf console verbosity: concise | normal (default) | verbose. Concise collapses each section's per-script lines into per-category counts. Does not affect the WhatIf summary file.");
        Console.WriteLine("  --report:<basepath>              Write the deployment summary to <basepath>.json and <basepath>.md instead of the log directory. Give the path without an extension.");
    }
}
