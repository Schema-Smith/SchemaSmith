// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
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
/// A real extraction of a table whose schema, name, column and check hold every character a name can carry that SQL
/// treats specially: both brackets, a quote, a percent sign, a dot, a backslash, a comma, a space and a letter outside
/// the database's code page. The table is in the package, under its exact name, with its check.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class HostileNameExtractionTests
{
    private const string H = "a'b%c[d]e.f\\g,h i éΩ";
    private const string Schema = "s" + H, Table = "t" + H, Column = "c" + H, Check = "k" + H;

    private string _db = "";
    private string _masterConnectionString = "";
    private string _productPath = "";

    private static string B(string name) => "[" + name.Replace("]", "]]") + "]";

    [OneTimeSetUp]
    public void CreateTheDatabase()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"],
            ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"));
        _db = $"TongsHostile_{Guid.NewGuid():N}"[..30];
        Run(null, $"CREATE DATABASE [{_db}]");
        Run(_db, $"EXEC(N'CREATE SCHEMA {B(Schema).Replace("'", "''")}')");
        Run(_db, $"CREATE TABLE {B(Schema)}.{B(Table)} ({B(Column)} INT NOT NULL PRIMARY KEY, CONSTRAINT {B(Check)} CHECK ({B(Column)} >= 0))");
    }

    [OneTimeTearDown]
    public void DropTheDatabase()
    {
        using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            global::Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _db);
        }
        if (Directory.Exists(_productPath)) Directory.Delete(_productPath, true);
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void TheTable_IsExtractedUnderItsExactName_WithItsCheck()
    {
        _productPath = Path.Join(Path.GetTempPath(), $"TongsHostile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_productPath);
        var errors = new List<string>();
        var errorLog = Substitute.For<ILog>();
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());

            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }

        var tablesDir = Path.Join(_productPath, "Templates", "Main", "Tables");
        var tables = Directory.Exists(tablesDir) ? Directory.GetFiles(tablesDir, "*.json").Select(File.ReadAllText).ToList() : [];
        var table = tables.SingleOrDefault(t => t.Contains("\"Name\": \"" + B(Table).Replace("\\", "\\\\")));
        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty, "the extraction reported errors");
            Assert.That(table, Is.Not.Null, $"no table file holds the name; written: {string.Join(" || ", tables)}");
            Assert.That(table ?? "", Does.Contain(Check.Replace("\\", "\\\\")), "the check, read for the table it belongs to");
        });
    }

    private void Run(string database, string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        if (database != null) conn.ChangeDatabase(database);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
            ["ScriptTokens:MainDB"] = _db,
            ["Product:Path"] = _productPath,
            ["Product:Name"] = "HostileProduct",
            ["Product:CheckConstraintStyle"] = "TableLevel",
            ["Template:Name"] = "Main",
            ["ShouldCast:Tables"] = "true",
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
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
