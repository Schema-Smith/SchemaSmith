// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
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
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// A numbered procedure (dbo.p;2 and on) shares its name with dbo.p, and SchemaTongs reads only that one's definition, so
/// each later number was lost without a word. They are named in a warning and counted as skipped.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class NumberedProcedureExtractionTests
{
    private const string ProductName = "NumProcProduct";
    private const string TemplateName = "Main";

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
        _db = NewDatabase("TongsNum");
        OnDb(_db, cmd =>
        {
            Run(cmd, "CREATE PROCEDURE dbo.Num AS SELECT 1 AS one");
            Run(cmd, "CREATE PROCEDURE dbo.Num;2 AS SELECT 2 AS two");
            Run(cmd, "CREATE PROCEDURE dbo.Plain AS SELECT 3 AS three");
        });
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
    public void ANumberedProcedure_IsNamedInAWarning_AndItsFirstNumberStillExtracts()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsNum_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);
        var progressLog = Substitute.For<ILog>();
        var exitCode = Extract(progressLog);
        var procedures = Path.Join(_tempProductPath, "Templates", TemplateName, "Procedures");
        var warnings = progressLog.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Warn").Select(c => c.GetArguments()[0]?.ToString() ?? "").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Join(procedures, "dbo.Num.sql")), Does.Contain("one").And.Not.Contain("two"), "the ;1 definition");
            Assert.That(File.Exists(Path.Join(procedures, "dbo.Plain.sql")), Is.True);
            Assert.That(warnings.Count(w => w.Contains("dbo.Num;2") && w.Contains("numbered procedure")), Is.EqualTo(1), "the lost number is named");
            Assert.That(warnings.Any(w => w.Contains("Plain")), Is.False);
            Assert.That(exitCode, Is.EqualTo(1), "an object the package misses makes the run exit 1");
        });
    }

    private int Extract(ILog progressLog)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();
            return tongs.ExitCode;
        }
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
            ["ShouldCast:Procedures"] = "true",
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
            ["ShouldCast:SecurityPolicies"] = "false",
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
