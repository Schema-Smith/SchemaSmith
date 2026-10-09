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

    [TestCase("1")]
    [TestCase("4")]
    public void EveryDatabaseGetsEveryScript_WithItsOwnQueryTokenValues(string maxThreads)
    {
        var prefix = "fanout_" + Guid.NewGuid().ToString("N")[..8];
        var dbs = Enumerable.Range(1, 3).Select(i => $"{prefix}_db{i}").ToArray();
        var tempDir = Path.Join(Path.GetTempPath(), $"{prefix}_pkg");

        lock (FactoryContainer.SharedLockObject)
        {
            _progressLog.ClearReceivedCalls();
            _errorLog.ClearReceivedCalls();
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
                ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
                    config["Target:User"], config["Target:Password"], config["Target:Port"], connProps));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var savedPath = config["SchemaPackagePath"];
            var savedThreads = config[SettingsKeys.MaxThreads];

            try
            {
                foreach (var db in dbs)
                {
                    cmd.CommandText = $"CREATE DATABASE [{db}]";
                    cmd.ExecuteNonQuery();
                }
                WritePackage(tempDir, prefix);
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
                try
                {
                    conn.ChangeDatabase("master");
                    foreach (var db in dbs)
                    {
                        cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (DbException) { /* best-effort cleanup */ }
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
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
