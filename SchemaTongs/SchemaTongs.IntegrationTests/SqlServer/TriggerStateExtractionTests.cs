// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// A trigger's enabled state and its first/last order are not part of its definition, so a disabled trigger or one
/// set first or last used to come back enabled and unordered on the next deploy. The extracted script now carries
/// a DISABLE TRIGGER and sp_settriggerorder tail for each, validates clean against the source, and deploys twice
/// to a target that ends in the source's state.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
public class TriggerStateExtractionTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private string _targetDb = "";
    private readonly Dictionary<string, string> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"],
            ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"TrgStateSrc_{unique}";
        _targetDb = $"TrgStateDst_{unique}";
        CreateDatabases();
        Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void TheScripts_CarryTheStateAsTails_AndValidateCleanAgainstTheSource()
    {
        Assert.That(_scripts.Keys, Is.SupersetOf(new[] { "Triggers/dbo.T.trDisabled", "Triggers/dbo.T.trOrdered", "Triggers/dbo.T.trPlain", "DDLTriggers/trDdlOff" }), "extracted: " + string.Join(", ", _scripts.Keys));
        Assert.Multiple(() =>
        {
            Assert.That(_scripts["Triggers/dbo.T.trDisabled"], Does.Contain("DISABLE TRIGGER [dbo].[trDisabled] ON [dbo].[T]"), _scripts["Triggers/dbo.T.trDisabled"]);
            Assert.That(_scripts["Triggers/dbo.T.trOrdered"], Does.Contain("EXEC sp_settriggerorder @triggername = N'[dbo].[trOrdered]', @order = N'First', @stmttype = N'INSERT'")
                .And.Contain("EXEC sp_settriggerorder @triggername = N'[dbo].[trOrdered]', @order = N'Last', @stmttype = N'UPDATE'"), _scripts["Triggers/dbo.T.trOrdered"]);
            Assert.That(_scripts["Triggers/dbo.T.trPlain"], Does.Not.Contain("DISABLE TRIGGER").And.Not.Contain("sp_settriggerorder"), "a plain trigger carries no tail");
            Assert.That(_scripts["DDLTriggers/trDdlOff"], Does.Contain("DISABLE TRIGGER [trDdlOff] ON DATABASE"), _scripts["DDLTriggers/trDdlOff"]);
            Assert.That(_warnings.Where(w => w.Contains("Invalid script")), Is.Empty, "validation against the source must accept the tails: " + string.Join(" | ", _warnings));
        });
    }

    [Test]
    public void EachScript_DeploysTwice_AndTheTargetEndsInTheSourcesState()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        foreach (var key in new[] { "Triggers/dbo.T.trDisabled", "Triggers/dbo.T.trOrdered", "Triggers/dbo.T.trPlain", "DDLTriggers/trDdlOff" })
        {
            Assert.That(_scripts.TryGetValue(key, out var script), Is.True, $"{key} was not extracted");
            Deploy(cmd, script, key + " (creating)");
            Deploy(cmd, script, key + " (again)");
        }

        Assert.That(State(cmd), Is.EqualTo(State(OpenSourceCommand())),
            "the target's triggers must be in the source's state: disabled where disabled, first and last where ordered, and the DDL trigger off");
    }

    private IDbCommand OpenSourceCommand()
    {
        var conn = Open(_sourceDb);
        return conn.CreateCommand();
    }

    private static List<string> State(IDbCommand cmd)
    {
        cmd.CommandText = @"
SELECT t.name + ':' + CONVERT(VARCHAR(1), t.is_disabled)
     + ':' + ISNULL(CONVERT(VARCHAR(1), OBJECTPROPERTY(t.object_id, 'ExecIsFirstInsertTrigger')), '-')
     + ISNULL(CONVERT(VARCHAR(1), OBJECTPROPERTY(t.object_id, 'ExecIsLastUpdateTrigger')), '-')
  FROM sys.triggers t
 WHERE t.name IN ('trDisabled', 'trOrdered', 'trPlain', 'trDdlOff')
 ORDER BY t.name";
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static void Deploy(IDbCommand cmd, string script, string what)
    {
        foreach (var batch in SqlServerBatchSplitter.Split(script))
        {
            cmd.CommandText = batch;
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (DbException ex)
            {
                Assert.Fail($"{what} failed: {ex.Message}\nbatch:\n{batch}\nscript:\n{script}");
            }
        }
    }

    private IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(database);
        return conn;
    }

    private void Extract()
    {
        var file = Substitute.For<IFile>();
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci =>
        {
            var parts = ci.ArgAt<string>(0).Replace('\\', '/').Split('/');
            if (parts.Length < 2 || !parts[^1].EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return;
            _scripts[$"{parts[^2]}/{System.IO.Path.GetFileNameWithoutExtension(parts[^1])}"] = ci.ArgAt<string>(1);
        });
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => _warnings.Add(ci.ArgAt<object>(0)?.ToString()));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            foreach (var kind in new[] { "Tables", "Schemas", "UserDefinedTypes", "Catalogs", "StopLists", "XMLSchemaCollections", "IndexedViews", "Synonyms", "Sequences", "Views", "Functions", "Procedures" })
                config[$"ShouldCast:{kind}"] = "false";
            foreach (var kind in new[] { "TableTriggers", "DDLTriggers", "ValidateScripts" })
                config[$"ShouldCast:{kind}"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform.SqlServer).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private void CreateDatabases()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            Run(cmd, $"CREATE DATABASE [{db}]");
            conn.ChangeDatabase(db);
            Run(cmd, "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, V INT NULL)");
            conn.ChangeDatabase("master");
        }

        conn.ChangeDatabase(_sourceDb);
        Run(cmd, "CREATE TRIGGER dbo.trDisabled ON dbo.T AFTER INSERT AS SELECT 1");
        Run(cmd, "DISABLE TRIGGER dbo.trDisabled ON dbo.T");
        Run(cmd, "CREATE TRIGGER dbo.trOrdered ON dbo.T AFTER INSERT, UPDATE AS SELECT 2");
        Run(cmd, "EXEC sp_settriggerorder @triggername = N'dbo.trOrdered', @order = N'First', @stmttype = N'INSERT'");
        Run(cmd, "EXEC sp_settriggerorder @triggername = N'dbo.trOrdered', @order = N'Last', @stmttype = N'UPDATE'");
        Run(cmd, "CREATE TRIGGER dbo.trPlain ON dbo.T AFTER DELETE AS SELECT 3");
        Run(cmd, "CREATE TRIGGER trDdlOff ON DATABASE FOR CREATE_TABLE AS SELECT 4");
        Run(cmd, "DISABLE TRIGGER trDdlOff ON DATABASE");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
