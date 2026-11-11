// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Deploys a throwaway package through DatabaseQuench against the suite's main database, for fixtures that assert on
/// what a run of object scripts does. The package is written under a temp folder, the deploy runs with the engine's
/// test connection and in-memory logs, and the result carries each script's outcome keyed by its folder, the run's
/// change audit, and the log lines an assertion can quote. The subclass supplies the engine.
/// </summary>
public abstract class ObjectScriptDeployFixture
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string ProductName { get; }

    protected IDbConnection Connection { get; private set; } = null!;
    protected string PackageDir { get; private set; } = "";
    protected string TemplateDir => Path.Join(PackageDir, "Templates", "Main");

    protected sealed record DeployResult(Dictionary<string, ScriptOutcome> Outcomes, IReadOnlyList<ChangeAuditRow> Audit, List<string> Log,
        FailureRecord Failure = null, IReadOnlyList<LockWait> LockWaits = null)
    {
        public string Describe() => string.Join(", ", Outcomes.Select(kv => $"{kv.Key}={kv.Value}"));
        public string DescribeAudit() => string.Join(", ", Audit.Select(r => $"{r.ObjectType} {r.ObjectName} {r.Action}"));
    }

    /// <summary>A connection string from the engine's own test settings, landing on the main database.</summary>
    protected string TestConnection(string initialDatabase)
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        return ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1", initialDatabase, config[$"{ConfigPrefix}:User"], config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"],
            ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"));
    }

    [OneTimeSetUp]
    public void OpenConnectionAndPackageFolder()
    {
        Connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        Connection.Open();
        if (Platform.GetBasePlatform() != Platform.MySQL) Connection.ChangeDatabase(MainDb);
        PackageDir = Path.Join(Path.GetTempPath(), $"{GetType().Name}_{Guid.NewGuid():N}");
    }

    // Runs after the subclass's own OneTimeTearDown, which is where its objects are dropped.
    [OneTimeTearDown]
    public void CloseConnectionAndDeletePackageFolder()
    {
        Connection?.Close();
        Connection?.Dispose();
        if (Directory.Exists(PackageDir)) Directory.Delete(PackageDir, true);
    }

    protected void Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    protected object Scalar(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return TransientRetry.Run(() => cmd.ExecuteScalar());
    }

    protected long Count(string sql) => Convert.ToInt64(Scalar(sql));

    /// <summary>
    /// Starts the package over: Product.json, a Main template whose ScriptFolders are the given JSON array body, and
    /// an empty folder for each name (Tables is always there).
    /// </summary>
    protected void WritePackage(string scriptFoldersJson, params string[] folders) => WritePackageWith("", scriptFoldersJson, folders);

    /// <param name="templateJson">More Template.json properties, written after ScriptFolders (", \"ScriptTokens\": { ... }").</param>
    protected void WritePackageWith(string templateJson, string scriptFoldersJson, params string[] folders)
    {
        if (Directory.Exists(PackageDir)) Directory.Delete(PackageDir, true);
        foreach (var folder in folders.Append("Tables")) Directory.CreateDirectory(Path.Join(TemplateDir, folder));
        File.WriteAllText(Path.Join(PackageDir, "Product.json"), $$"""
            { "Name": "{{ProductName}}", "Platform": "{{Platform}}", "TemplateOrder": [ "Main" ] }
            """);
        File.WriteAllText(Path.Join(TemplateDir, "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT '{{MainDb}}'", "ScriptFolders": [ {{scriptFoldersJson}} ]{{templateJson}} }
            """);
    }

    protected void WriteFile(string folder, string fileName, string text) => File.WriteAllText(Path.Join(TemplateDir, folder, fileName), text);

    /// <param name="expectSuccess">False for a deploy that is meant to stop: the result is returned instead of asserted on.</param>
    /// <param name="checkpointDir">A checkpoint directory the caller owns and keeps between deploys, for a resumed run.</param>
    /// <param name="resume">Adopt the checkpoint a previous deploy left, as --ResumeQuench does.</param>
    protected DeployResult Deploy(bool skipUnchanged = true, bool whatIf = false, bool runTwice = false, bool expectSuccess = true, bool dropSchemaBoundDependents = false,
        bool updateTables = true, (string User, string Password)? login = null, bool dropRemovedTables = false,
        string checkpointDir = null, bool resume = false, IReadOnlyDictionary<string, string> settings = null, bool keepColumns = false,
        bool deliverData = false)
    {
        var ownsCheckpointDir = checkpointDir == null;
        checkpointDir ??= Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
        lock (FactoryContainer.SharedLockObject)
        {
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                var values = new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = PackageDir,
                    ["Target:User"] = login?.User ?? config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = login?.Password ?? config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"],
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1",
                    ["Target:SkipUnchangedObjects"] = skipUnchanged ? "true" : "false"
                };
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                    values[key] = value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(values!).Build());

                var product = Product.Load();
                var template = Template.Load("Main", product);
                // The deploy's own log, so a failed step says why in the assertion rather than in a file nobody reads.
                var log = new List<string>();
                var progressLog = Substitute.For<log4net.ILog>();
                progressLog.When(l => l.Info(Arg.Any<object>())).Do(ci => log.Add(ci.ArgAt<object>(0)?.ToString()));
                progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => log.Add("WARN " + ci.ArgAt<object>(0)));
                progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0)));
                var errorLog = Substitute.For<log4net.ILog>();
                errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0)));
                errorLog.When(l => l.Error(Arg.Any<object>(), Arg.Any<Exception>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0) + " " + ci.ArgAt<Exception>(1)?.Message));
                LogFactory.Register("ProgressLog", progressLog);
                LogFactory.Register("ErrorLog", errorLog);
                var audit = new ChangeAuditCapture();
                var contention = new ContentionLog();
                var quench = new DatabaseQuench(product, template, new DatabaseQuenchOptions { Server = "127.0.0.1", DatabaseName = MainDb, SuppressKindling = login != null, WhatIf = whatIf, RunScriptsTwice = runTwice, UpdateTables = updateTables, DeliverData = deliverData, Drops = new DropPolicy(Tables: dropRemovedTables, Columns: !keepColumns, ForeignKeys: true, CheckConstraints: true, ExcludeConstraints: true, Statistics: true, Indexes: true), Destructive = DestructivePolicy.Read(FactoryContainer.Resolve<IConfigurationRoot>()) }, new FileCheckpointManager(checkpointDir, resume)) { ChangeAudit = audit, DropSchemaBoundDependents = dropSchemaBoundDependents, Contention = contention };
                quench.Execute();
                var scripts = quench.IterationObjectScripts.Concat(quench.IterationAfterTablesObjectScripts).ToList();
                var errors = scripts.Where(s => s.Error != null).Select(s => $"{Path.GetFileName(s.LogPath)}: {s.Error.Message}");
                if (expectSuccess) Assert.That(quench.QuenchSuccessful, Is.True, "the deploy must succeed; script errors: " + string.Join(" | ", errors)
                    + "\nlog: " + string.Join("\n", log.Where(l => l != null && (l.StartsWith("ERROR") || l.StartsWith("WARN") || l.Contains("Quenching")))));

                var outcomes = new Dictionary<string, ScriptOutcome>();
                foreach (var script in scripts)
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(script.LogPath) ?? "");
                    outcomes[folder] = script.Outcome;
                }
                return new DeployResult(outcomes, audit.Snapshot(), log, quench.LastFailure, contention.Snapshot());
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (ownsCheckpointDir && Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    protected sealed record DriftRun(int ExitCode, DriftReport Report, List<string> Log, string Refusal = null, int? DriftTier = null)
    {
        public string Describe() => $"exit {ExitCode}; {Refusal}; changes: "
                                    + string.Join(", ", Report?.Targets.SelectMany(t => t.Changes).Select(c => $"{c.ObjectType} {c.ObjectName} {c.Action}") ?? [])
                                    + "; " + string.Join(" | ", Log.Where(l => l != null && (l.StartsWith("ERROR") || l.StartsWith("WARN") || l.Contains("rift"))));
    }

    /// <summary>
    /// Runs the whole product through ProductQuench with <paramref name="switches"/> on the command line (--Drift by default),
    /// as SchemaQuench.exe would, and returns the exit code it would end with and the drift verdict.
    /// </summary>
    protected DriftRun RunDrift(string switches = "--Drift", IReadOnlyDictionary<string, string> settings = null)
    {
        var reportBase = Path.Join(Path.GetTempPath(), $"DriftReport_{Guid.NewGuid():N}");
        var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
        lock (FactoryContainer.SharedLockObject)
        {
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            // A run of the tool is a process of its own: it decides from its own command line whether to take up a checkpoint,
            // and finds none another test left. A manager registered by an earlier test (one built for --ResumeQuench) would
            // otherwise resume this run and skip its scripts.
            var savedCheckpointing = FactoryContainer.Resolve<ICheckpointing>();
            FactoryContainer.Register<ICheckpointing>(new FileCheckpointManager(checkpointDir, switches.Contains("--ResumeQuench")));
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                var values = new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = PackageDir,
                    ["Target:User"] = config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"],
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1",
                };
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                    values[key] = value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(values!).Build());

                int? exited = null;
                var environment = Substitute.For<IEnvironment>();
                environment.CommandLine.Returns($"SchemaQuench.exe {switches} --report:{reportBase}");
                environment.When(e => e.Exit(Arg.Any<int>())).Do(ci => exited ??= ci.ArgAt<int>(0));
                FactoryContainer.Register(environment);

                var log = new List<string>();
                var progressLog = Substitute.For<log4net.ILog>();
                progressLog.When(l => l.Info(Arg.Any<object>())).Do(ci => log.Add(ci.ArgAt<object>(0)?.ToString()));
                progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => log.Add("WARN " + ci.ArgAt<object>(0)));
                progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0)));
                var errorLog = Substitute.For<log4net.ILog>();
                errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0)));
                errorLog.When(l => l.Error(Arg.Any<object>(), Arg.Any<Exception>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0) + " " + ci.ArgAt<Exception>(1)?.Message));
                LogFactory.Register("ProgressLog", progressLog);
                LogFactory.Register("ErrorLog", errorLog);

                ProductQuench product;
                try
                {
                    product = new ProductQuench();
                }
                catch (RunFailedException e)
                {
                    return new DriftRun(2, null, log, e.Message);
                }
                try
                {
                    product.QuenchProduct();
                }
                catch (RunFailedException e)
                {
                    SummaryConformance.AssertConforms(reportBase + ".json");
                    return new DriftRun(2, product.DriftReport, log, e.Message, product.DriftTier);
                }
                SummaryConformance.AssertConforms(reportBase + ".json");
                return new DriftRun(exited ?? product.ExitCode, product.DriftReport, log, null, product.DriftTier);
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                FactoryContainer.Unregister<ICheckpointing>();
                if (savedCheckpointing != null) FactoryContainer.Register(savedCheckpointing);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
                foreach (var ext in new[] { ".json", ".md" })
                    if (File.Exists(reportBase + ext)) File.Delete(reportBase + ext);
            }
        }
    }
}
