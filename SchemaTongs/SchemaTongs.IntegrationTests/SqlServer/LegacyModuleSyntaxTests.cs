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
/// Under ShouldCast:ModuleSyntax = Legacy every module is written as a guarded CREATE stub followed by ALTER, the form
/// SQL Server builds before 2016 SP1 accept. The scripts are extracted with validation on, then each is deployed into an
/// empty target twice (once creating, once altering), and the target's catalog carries the real body.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
public class LegacyModuleSyntaxTests
{
    private static readonly string[] ModuleScripts =
    [
        "Views/dbo.vT", "Functions/dbo.fScalar", "Functions/dbo.fInline", "Functions/dbo.fMulti",
        "Procedures/dbo.pOne", "Triggers/dbo.T.trT", "Triggers/dbo.vT.trV", "DDLTriggers/trDdl"
    ];

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
        _sourceDb = $"LegacySrc_{unique}";
        _targetDb = $"LegacyDst_{unique}";
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
    public void EveryModuleScript_AvoidsCreateOrAlter_AndValidatesCleanAgainstTheSource()
    {
        Assert.That(_scripts.Keys, Is.SupersetOf(ModuleScripts), "extracted: " + string.Join(", ", _scripts.Keys));
        Assert.Multiple(() =>
        {
            foreach (var key in ModuleScripts)
                Assert.That(_scripts[key], Does.Not.Contain("CREATE OR ALTER").IgnoreCase, $"{key} must be written in the legacy form:\n{_scripts[key]}");
            Assert.That(_warnings.Where(w => w.Contains("Invalid script")), Is.Empty, "validation against the source must accept the legacy form");
        });
    }

    [Test]
    public void EachScript_DeploysTwice_AndTheTargetCarriesTheRealBody()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        foreach (var key in ModuleScripts)
        {
            Assert.That(_scripts.TryGetValue(key, out var script), Is.True, $"{key} was not extracted; extracted: {string.Join(", ", _scripts.Keys)}");
            Deploy(cmd, script, key + " (creating)");
            // The object exists now: the guard skips the stub and ALTER carries the body again.
            Deploy(cmd, script, key + " (altering)");
        }

        cmd.CommandText = @"
SELECT o.name COLLATE DATABASE_DEFAULT + ':' + RTRIM(o.type) COLLATE DATABASE_DEFAULT + ':' + CASE WHEN sm.definition LIKE '%/* real body */%' THEN 'real' ELSE 'stub' END
  FROM sys.objects o JOIN sys.sql_modules sm ON sm.object_id = o.object_id
 WHERE o.name IN ('vT', 'fScalar', 'fInline', 'fMulti', 'pOne', 'trT', 'trV')
UNION ALL
SELECT t.name COLLATE DATABASE_DEFAULT + ':DDL:' + CASE WHEN sm.definition LIKE '%/* real body */%' THEN 'real' ELSE 'stub' END
  FROM sys.triggers t JOIN sys.sql_modules sm ON sm.object_id = t.object_id
 WHERE t.parent_class = 0 AND t.name = 'trDdl'";
        var modules = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) modules.Add(reader.GetString(0));
        Assert.That(modules, Is.EquivalentTo(new[]
        {
            "vT:V:real", "fScalar:FN:real", "fInline:IF:real", "fMulti:TF:real", "pOne:P:real", "trT:TR:real", "trV:TR:real", "trDdl:DDL:real"
        }), "a module kept its stub body or lost its kind; scripts:\n" + string.Join("\n----\n", ModuleScripts.Select(k => _scripts.GetValueOrDefault(k))));
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
            foreach (var kind in new[] { "Tables", "Schemas", "UserDefinedTypes", "Catalogs", "StopLists", "XMLSchemaCollections", "IndexedViews", "Synonyms", "Sequences" })
                config[$"ShouldCast:{kind}"] = "false";
            foreach (var kind in new[] { "Views", "Functions", "Procedures", "TableTriggers", "DDLTriggers", "ValidateScripts" })
                config[$"ShouldCast:{kind}"] = "true";
            config["ShouldCast:ModuleSyntax"] = "Legacy";
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
        Run(cmd, "CREATE VIEW dbo.vT AS /* real body */ SELECT Id, V FROM dbo.T");
        Run(cmd, "CREATE FUNCTION dbo.fScalar(@a INT) RETURNS INT AS BEGIN /* real body */ RETURN @a + 1 END");
        Run(cmd, "CREATE FUNCTION dbo.fInline() RETURNS TABLE AS RETURN /* real body */ (SELECT Id FROM dbo.T)");
        Run(cmd, "CREATE FUNCTION dbo.fMulti() RETURNS @r TABLE (Id INT) AS BEGIN /* real body */ INSERT @r SELECT Id FROM dbo.T RETURN END");
        Run(cmd, "CREATE PROCEDURE dbo.pOne @x INT AS /* real body */ SELECT @x");
        Run(cmd, "CREATE TRIGGER dbo.trT ON dbo.T AFTER UPDATE AS /* real body */ SELECT 1");
        Run(cmd, "CREATE TRIGGER dbo.trV ON dbo.vT INSTEAD OF DELETE AS /* real body */ SELECT 1");
        Run(cmd, "CREATE TRIGGER trDdl ON DATABASE FOR CREATE_TABLE AS /* real body */ SELECT 1");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
