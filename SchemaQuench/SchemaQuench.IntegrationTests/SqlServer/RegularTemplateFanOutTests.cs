// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Configuration;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// #475. A regular template whose DatabaseIdentificationScript returns several databases: every database must get
/// every script, each with its own query-token values. Every database's pass shared one set of script objects, so a
/// script applied in one database was skipped in the rest (reported as success), and the first database's resolved
/// query tokens were written into the scripts every later database ran. One thread makes the skip deterministic.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class RegularTemplateFanOutTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    // Created on the first case and reused by the second, which starts by removing what the first deployed: a new
    // database costs CREATE DATABASE and a full kindle, three times over, and the defect is in how scripts reach each
    // database, not in a cold one.
    private readonly string _prefix = "fanout_" + Guid.NewGuid().ToString("N")[..8];
    private bool _databasesCreated;
    private string[] Databases => Enumerable.Range(1, 3).Select(i => $"{_prefix}_db{i}").ToArray();

    [OneTimeTearDown]
    public void DropDatabases()
    {
        if (!_databasesCreated) return;
        using var conn = OpenMaster();
        using var cmd = conn.CreateCommand();
        try
        {
            foreach (var db in Databases)
            {
                Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
            }
        }
        catch (DbException) { /* best-effort cleanup */ }
    }

    [TestCase("1")]
    [TestCase("4")]
    public void EveryDatabaseGetsEveryScript_WithItsOwnQueryTokenValues(string maxThreads)
    {
        var dbs = Databases;
        var tempDir = Path.Join(Path.GetTempPath(), $"{_prefix}_{maxThreads}_pkg");

        lock (FactoryContainer.SharedLockObject)
        {
            _progressLog.ClearReceivedCalls();
            _errorLog.ClearReceivedCalls();
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            using var conn = OpenMaster();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var savedPath = config["SchemaPackagePath"];
            var savedThreads = config[SettingsKeys.MaxThreads];

            try
            {
                foreach (var db in dbs)
                {
                    cmd.CommandText = _databasesCreated
                        ? $"USE [{db}]; DROP PROCEDURE IF EXISTS dbo.FanoutProbe; DROP TABLE IF EXISTS dbo.AfterMarker; "
                          + "DELETE SchemaSmith.CompletedMigrationScripts WHERE ProductName = 'FanoutProbe'; USE master;"
                        : $"CREATE DATABASE [{db}]";
                    cmd.ExecuteNonQuery();
                }
                _databasesCreated = true;
                WritePackage(tempDir, _prefix);
                config["SchemaPackagePath"] = tempDir;
                config[SettingsKeys.MaxThreads] = maxThreads;

                Program.Main([]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));

                foreach (var db in dbs)
                {
                    conn.ChangeDatabase(db);
                    cmd.CommandText = "SELECT OBJECT_ID('dbo.FanoutProbe', 'P')";
                    Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value), $"{db} must get the Objects-slot procedure");
                    cmd.CommandText = "EXEC dbo.FanoutProbe";
                    Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(db), $"{db}'s procedure must carry {db}'s own query-token value");
                    cmd.CommandText = "SELECT OBJECT_ID('dbo.AfterMarker', 'U')";
                    Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value), $"{db} must get the After-slot script");
                }
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                config[SettingsKeys.MaxThreads] = savedThreads;
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

    // LogFormat json: four units at once, and each line's target fields are the unit it came from. Rendered as each event
    // is logged, on the logging thread, as the tools' own appenders do.
    [Test]
    public void StructuredLogLines_CarryTheirOwnUnitsTarget_WhenUnitsRunInParallel()
    {
        var dbs = Databases;
        var tempDir = Path.Join(Path.GetTempPath(), $"{_prefix}_json_pkg");
        var lines = new StringWriter();
        string summaryRunId = null;
        var repository = (log4net.Repository.Hierarchy.Hierarchy)LogManager.CreateRepository($"fanout-json-{Guid.NewGuid():N}");
        var appender = new log4net.Appender.TextWriterAppender { Layout = new JsonLayout(), Writer = TextWriter.Synchronized(lines), ImmediateFlush = true };
        appender.ActivateOptions();
        ((log4net.Repository.Hierarchy.Logger)repository.GetLogger("ProgressLog")).AddAppender(appender);
        repository.Configured = true;

        lock (FactoryContainer.SharedLockObject)
        {
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", LogManager.GetLogger(repository.Name, "ProgressLog"));
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            using var conn = OpenMaster();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var savedPath = config["SchemaPackagePath"];
            var savedThreads = config[SettingsKeys.MaxThreads];
            try
            {
                foreach (var db in dbs)
                {
                    cmd.CommandText = _databasesCreated
                        ? $"USE [{db}]; DROP PROCEDURE IF EXISTS dbo.FanoutProbe; DROP TABLE IF EXISTS dbo.AfterMarker; "
                          + "DELETE SchemaSmith.CompletedMigrationScripts WHERE ProductName = 'FanoutProbe'; USE master;"
                        : $"CREATE DATABASE [{db}]";
                    cmd.ExecuteNonQuery();
                }
                _databasesCreated = true;
                WritePackage(tempDir, _prefix);
                config["SchemaPackagePath"] = tempDir;
                config[SettingsKeys.MaxThreads] = "4";

                Program.Main([]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));
                summaryRunId = (string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")))["run"]!["id"];
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                config[SettingsKeys.MaxThreads] = savedThreads;
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                repository.Shutdown();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }

        var parsed = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(l,
                new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })!)
            .ToList();
        Assert.That(parsed, Has.Count.GreaterThan(10), "structured lines written");
        // One run, one id: the log lines and the summary the same run wrote.
        Assert.That(parsed.Select(l => (string)l["runId"]).Distinct(), Is.EqualTo(new[] { summaryRunId }));
        foreach (var db in dbs)
        {
            var own = parsed.Where(l => (string)l["database"] == db).ToList();
            Assert.That(own, Has.Count.GreaterThanOrEqualTo(3), $"lines scoped to {db}");
            var others = dbs.Where(d => d != db).ToList();
            var crossed = own.Select(l => (string)l["message"]).Where(m => others.Any(o => m.Contains(o, StringComparison.Ordinal))).ToList();
            Assert.That(crossed, Is.Empty, $"lines scoped to {db} that are about another database");
        }
        var written = parsed.Single(l => ((string)l["message"]).StartsWith("Deployment summary written", StringComparison.Ordinal));
        Assert.That(written.ContainsKey("database"), Is.False, "a product-level line after the units carries no target");
    }

    private static IDbConnection OpenMaster()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
            ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
                config["Target:User"], config["Target:Password"], config["Target:Port"], connProps));
        conn.Open();
        return conn;
    }

    private static void WritePackage(string dir, string prefix)
    {
        var template = Path.Join(dir, "Templates", "Fanout");
        Directory.CreateDirectory(Path.Join(template, "Procedures"));
        Directory.CreateDirectory(Path.Join(template, "After"));
        File.WriteAllText(Path.Join(dir, "Product.json"),
            "{ \"Name\": \"FanoutProbe\", \"ValidationScript\": \"SELECT CAST(1 AS BIT)\", \"TemplateOrder\": [\"Fanout\"], "
            + "\"ScriptTokens\": {}, \"ScriptFolders\": [], \"Platform\": \"SqlServer\" }");
        File.WriteAllText(Path.Join(template, "Template.json"), $$"""
            { "Name": "Fanout",
              "DatabaseIdentificationScript": "SELECT [name] FROM sys.databases WHERE [name] LIKE '{{prefix}}[_]db%' ORDER BY [name]",
              "ScriptTokens": { "Who": "<*Query*>SELECT DB_NAME()" },
              "ScriptFolders": [ { "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" },
                                 { "FolderPath": "After", "QuenchSlot": "After" } ] }
            """);
        File.WriteAllText(Path.Join(template, "Procedures", "dbo.FanoutProbe.sql"),
            "CREATE OR ALTER PROCEDURE dbo.FanoutProbe AS SELECT N'{{Who}}' AS deployed_to");
        File.WriteAllText(Path.Join(template, "After", "AfterMarker.sql"),
            "IF OBJECT_ID('dbo.AfterMarker', 'U') IS NULL CREATE TABLE dbo.AfterMarker (Id INT NULL)");
    }
}
