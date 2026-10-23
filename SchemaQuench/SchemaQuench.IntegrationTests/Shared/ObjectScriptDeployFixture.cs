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

    protected sealed record DeployResult(Dictionary<string, ScriptOutcome> Outcomes, IReadOnlyList<ChangeAuditRow> Audit, List<string> Log)
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
    protected void WritePackage(string scriptFoldersJson, params string[] folders)
    {
        if (Directory.Exists(PackageDir)) Directory.Delete(PackageDir, true);
        foreach (var folder in folders.Append("Tables")) Directory.CreateDirectory(Path.Join(TemplateDir, folder));
        File.WriteAllText(Path.Join(PackageDir, "Product.json"), $$"""
            { "Name": "{{ProductName}}", "Platform": "{{Platform}}", "TemplateOrder": [ "Main" ] }
            """);
        File.WriteAllText(Path.Join(TemplateDir, "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT '{{MainDb}}'", "ScriptFolders": [ {{scriptFoldersJson}} ] }
            """);
    }

    protected void WriteFile(string folder, string fileName, string text) => File.WriteAllText(Path.Join(TemplateDir, folder, fileName), text);

    /// <param name="expectSuccess">False for a deploy that is meant to stop: the result is returned instead of asserted on.</param>
    protected DeployResult Deploy(bool skipUnchanged = true, bool whatIf = false, bool runTwice = false, bool expectSuccess = true, bool dropSchemaBoundDependents = false,
        bool updateTables = true, (string User, string Password)? login = null, bool dropRemovedTables = false)
    {
        var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
        lock (FactoryContainer.SharedLockObject)
        {
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                var settings = new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = PackageDir,
                    ["Target:User"] = login?.User ?? config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = login?.Password ?? config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"],
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1",
                    ["Target:SkipUnchangedObjects"] = skipUnchanged ? "true" : "false"
                };
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    settings[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(settings!).Build());

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
                // The PostgreSQL quench renders these as booleans, the others as 0/1.
                var no = Platform == Platform.PostgreSQL ? "false" : "0";
                var yes = Platform == Platform.PostgreSQL ? "true" : "1";
                var audit = new ChangeAuditCapture();
                var quench = new DatabaseQuench("127.0.0.1", product, template, MainDb,
                    suppressKindling: login != null, whatIfOnly: whatIf ? yes : no, runScriptsTwice: runTwice,
                    dropRemovedTables: dropRemovedTables ? yes : no, dropRemovedColumns: yes, dropRemovedForeignKeys: yes, dropRemovedCheckConstraints: yes, dropRemovedExcludeConstraints: yes, dropRemovedStatistics: yes, dropRemovedIndexes: yes, dropUnknownIndexes: false, updateTables: updateTables,
                    deliverData: false, checkpointing: new FileCheckpointManager(checkpointDir)) { ChangeAudit = audit, DropSchemaBoundDependents = dropSchemaBoundDependents };
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
                return new DeployResult(outcomes, audit.Snapshot(), log);
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }
}
