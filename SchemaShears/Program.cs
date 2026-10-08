// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Schema.Isolators;
using Schema.Configuration;
using Schema.Utility;

namespace SchemaShears;

public static class Program
{
    public static void Main(string[] args)
    {
        CommandLineParser.HandleCommonSwitches("SchemaShears", ToolSpecificSwitches);

        AppDomain.CurrentDomain.UnhandledException += UnhandledException;
        LogFactory.LogInitializer = ConfigHelper.ConfigureLog4Net;
        ConfigHelper.GetAppSettingsAndUserSecrets("SchemaShears", LogFactory.GetLogger("ProgressLog").Info);
        CommandLineParser.WarnOnUnrecognizedArguments(KnownArguments, LogFactory.GetLogger("ProgressLog").Warn);
        ConfigHelper.CheckStartupSettings(FactoryContainer.Resolve<IConfigurationRoot>(), LogFactory.GetLogger("ProgressLog").Warn);

        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        SettingsContract.WarnOnUnrecognizedKeys(config, SettingsTool.SchemaShears, LogFactory.GetLogger("ProgressLog").Warn);
        var allowDropsRaw = CommandLineParser.ValueOfSwitch("AllowDrops", config[SettingsKeys.AllowDrops]);
        var allowDrops = (allowDropsRaw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var request = new PatchBuildRequest
        {
            SourcePath = ResolveToFullPath(CommandLineParser.ValueOfSwitch("Source", config[SettingsKeys.SourcePath])),
            ManifestPath = ResolveToFullPath(CommandLineParser.ValueOfSwitch("Manifest", config[SettingsKeys.ManifestPath])),
            AlwaysIncludePath = ResolveToFullPath(CommandLineParser.ValueOfSwitch("AlwaysInclude", config[SettingsKeys.AlwaysIncludePath])),
            OutputPath = ResolveToFullPath(CommandLineParser.ValueOfSwitch("Output", config[SettingsKeys.OutputPath])),
            Zip = CommandLineParser.ContainsSwitch("Zip") || string.Equals(config[SettingsKeys.Zip], "true", StringComparison.OrdinalIgnoreCase),
            AllowDrops = allowDrops
        };

        new PatchBuilder().Build(request);
        LogBackup.BackupLogsAndExit("SchemaShears");
    }

    // Resolve a user-supplied path switch to an absolute path against the invocation directory, so a relative
    // --Source/--Manifest/--Output (the form the training labs document) works and logs/errors show canonical paths.
    // Path.GetFullPath resolves a relative path against the process CWD and leaves an absolute path unchanged.
    internal static string ResolveToFullPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path);

    public static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogBackup.UnhandledExceptionLogger("SchemaShears", e);
    }

    private static readonly string[] KnownArguments = { "Zip" };

    private static void ToolSpecificSwitches()
    {
        Console.WriteLine("  --Source:<path>          Full product folder to carve the patch from.");
        Console.WriteLine("  --Manifest:<path>        File listing product-relative paths to include (one per line).");
        Console.WriteLine("  --AlwaysInclude:<path>   Optional file listing paths/folders always included.");
        Console.WriteLine("  --Output:<path>          Destination folder for the patch package.");
        Console.WriteLine("  --Zip                    Also produce <Output>.zip.");
        Console.WriteLine("  --AllowDrops:<list>      Comma-separated drop categories to leave enabled (e.g. Columns,Indexes); empty suppresses all drops.");
    }
}
