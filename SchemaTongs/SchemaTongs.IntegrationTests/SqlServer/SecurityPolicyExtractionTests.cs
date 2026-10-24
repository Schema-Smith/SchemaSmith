// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
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
/// Row-level security policies were not extracted at all, so a package silently lost them. They are scripted to
/// <c>SecurityPolicies/</c>: a script creates the policy where it is missing and re-creates it, in one transaction, only
/// when its live definition differs, so a redeploy of an unchanged policy touches nothing.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class SecurityPolicyExtractionTests
{
    private const string ProductName = "RlsProduct";
    private const string TemplateName = "Main";
    private const string Function = "CREATE FUNCTION sec.fn_rep(@Rep AS SYSNAME) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS ok WHERE @Rep = USER_NAME() OR USER_NAME() = 'dbo'";

    private readonly List<string> _dbs = [];
    private string _db = "";
    private string _masterConnectionString = "";
    private string _tempProductPath = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = NewDatabase("TongsRls");
        OnDb(_db, CreateTableAndFunction);
        OnDb(_db, cmd => Run(cmd, """
            CREATE SECURITY POLICY sec.[Sales Filter]
              ADD FILTER PREDICATE sec.fn_rep(SalesRep) ON dbo.Sales,
              ADD BLOCK PREDICATE sec.fn_rep(SalesRep) ON dbo.Sales AFTER INSERT
              WITH (STATE = ON, SCHEMABINDING = ON) NOT FOR REPLICATION
            """));
    }

    private static void CreateTableAndFunction(IDbCommand cmd)
    {
        Run(cmd, "CREATE SCHEMA sec");
        Run(cmd, "CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, SalesRep SYSNAME NOT NULL)");
        Run(cmd, Function);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            foreach (var db in _dbs) Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
        }
        catch (DbException) { /* teardown must not mask an assertion */ }

        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void APolicyIsScripted_CreatedWhereMissing_LeftAloneWhenUnchanged_AndReplacedWhenItDiffers()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsRls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);
        Extract();
        var scriptFile = Path.Join(_tempProductPath, "Templates", TemplateName, "SecurityPolicies", "sec.Sales Filter.sql");
        Assert.That(File.Exists(scriptFile), Is.True, "the policy is scripted to SecurityPolicies/");
        var script = File.ReadAllText(scriptFile);

        var target = NewDatabase("TongsRlsTarget");
        OnDb(target, cmd =>
        {
            CreateTableAndFunction(cmd);
            // A deploy runs the scripts on one session, and a module scripted under QUOTED_IDENTIFIER OFF leaves it off;
            // the script's FOR XML signature query then failed and no policy was created.
            Run(cmd, "SET QUOTED_IDENTIFIER OFF");
            Run(cmd, script);
            Assert.That(Signature(cmd), Is.EqualTo(Signature(_db)), "created as the source has it");
            var created = PolicyId(cmd);

            Run(cmd, script);
            Assert.That(PolicyId(cmd), Is.EqualTo(created), "an unchanged policy is left alone");

            Run(cmd, "ALTER SECURITY POLICY sec.[Sales Filter] WITH (STATE = OFF)");
            Run(cmd, script);
            Assert.That(Signature(cmd), Is.EqualTo(Signature(_db)), "one that differs is replaced with the declared one");
        });
    }

    private void Extract()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }
    }

    private static int PolicyId(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT OBJECT_ID(N'sec.[Sales Filter]')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string Signature(string db)
    {
        var s = "";
        OnDb(db, cmd => s = Signature(cmd));
        return s;
    }

    private static string Signature(IDbCommand cmd)
    {
        cmd.CommandText = """
            SELECT CONCAT(p.is_enabled, p.is_schema_bound, p.is_not_for_replication, ' ',
                   (SELECT STRING_AGG(CAST(CONCAT(sp.predicate_type_desc, ':', sp.operation_desc, ':', OBJECT_NAME(sp.target_object_id), ':', sp.predicate_definition) AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT, '|')
                             WITHIN GROUP (ORDER BY sp.predicate_type_desc)
                      FROM sys.security_predicates sp WHERE sp.[object_id] = p.[object_id]))
              FROM sys.security_policies p WHERE p.[name] = N'Sales Filter'
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private string NewDatabase(string prefix)
    {
        var db = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"CREATE DATABASE [{db}]");
        _dbs.Add(db);
        return db;
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void OnDb(string db, Action<IDbCommand> act)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        conn.ChangeDatabase(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    private IConfigurationRoot BuildConfig()
    {
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root["SqlServer:Server"],
            ["Source:Port"] = root["SqlServer:Port"],
            ["Source:User"] = root["SqlServer:User"],
            ["Source:Password"] = root["SqlServer:Password"],
            ["Source:Database"] = _db,
            ["Target:Server"] = root["SqlServer:Server"],
            ["Target:Port"] = root["SqlServer:Port"],
            ["Target:User"] = root["SqlServer:User"],
            ["Target:Password"] = root["SqlServer:Password"],
            ["ScriptTokens:MainDB"] = _db,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:Tables"] = "false",
            ["ShouldCast:Views"] = "false",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:UserDefinedTypes"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:Schemas"] = "false",
            ["ShouldCast:IndexedViews"] = "false",
            ["ShouldCast:Sequences"] = "false",
            ["ShouldCast:Synonyms"] = "false",
            ["ShouldCast:XmlSchemaCollections"] = "false",
            ["ShouldCast:FullTextCatalogs"] = "false",
            ["ShouldCast:FullTextStopLists"] = "false",
            ["ShouldCast:SecurityPolicies"] = "true",
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
