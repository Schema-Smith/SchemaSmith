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

namespace SchemaTongs.IntegrationTests.Shared;

/// <summary>
/// A real extraction, on the MySQL family, of a database whose name holds a quote and a table whose name and column hold
/// a quote, a percent sign, the backtick, a dot, a backslash, a comma, a space and non-ASCII letters. The table is in the
/// package under its exact name, with its column.
/// </summary>
public abstract class HostileNameExtractionSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }

    private const string H = "a'b%c`d.e\\f,g h éΩ";
    private const string Table = "t" + H, Column = "c" + H;
    private string _db = "";
    private string _extractedTable = Table;
    private string _serverConnection = "";
    private string _productPath = "";

    private static string B(string name) => "`" + name.Replace("`", "``") + "`";

    [OneTimeSetUp]
    public void CreateTheDatabase()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _serverConnection = ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"], "mysql", config[$"{ConfigPrefix}:User"],
            config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"], ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"));
        _db = $"th'q_{Guid.NewGuid():N}"[..24];
        Run($"CREATE DATABASE {B(_db)}");
        Run($"CREATE TABLE {B(_db)}.{B(Table)} ({B(Column)} INT NOT NULL PRIMARY KEY) DEFAULT CHARSET=utf8mb4");
        // Under lower_case_table_names=1 the server stores the table name in lower case, so that is the name it has.
        if (Scalar("SELECT @@lower_case_table_names") == "1") _extractedTable = Table.ToLowerInvariant();
    }

    [OneTimeTearDown]
    public void DropTheDatabase()
    {
        Run($"DROP DATABASE IF EXISTS {B(_db)}");
        if (Directory.Exists(_productPath)) Directory.Delete(_productPath, true);
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void TheTable_IsExtractedUnderItsExactName_WithItsColumn()
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
            var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            var values = new Dictionary<string, string>
            {
                ["Source:Server"] = root[$"{ConfigPrefix}:Server"] ?? "127.0.0.1",
                ["Source:Port"] = root[$"{ConfigPrefix}:Port"],
                ["Source:User"] = root[$"{ConfigPrefix}:User"],
                ["Source:Password"] = root[$"{ConfigPrefix}:Password"],
                ["Source:Database"] = _db,
                ["Product:Path"] = _productPath,
                ["Product:Name"] = "HostileProduct",
                ["Template:Name"] = "Main",
                ["ShouldCast:Tables"] = "true",
            };
            foreach (var kind in new[] { "Views", "Functions", "Procedures", "TableTriggers", "Events", "Sequences", "ValidateScripts" })
                values[$"ShouldCast:{kind}"] = "false";
            foreach (var kv in ConnectionString.ReadProperties(root, $"{ConfigPrefix}:ConnectionProperties"))
                values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());

            new global::SchemaTongs.SchemaTongs(Platform).CastTemplate();
        }

        var tablesDir = Path.Join(_productPath, "Templates", "Main", "Tables");
        var tables = Directory.Exists(tablesDir) ? Directory.GetFiles(tablesDir, "*.json").Select(File.ReadAllText).ToList() : [];
        var table = tables.SingleOrDefault(t => t.Contains("\"Name\": \"" + B(_extractedTable).Replace("\\", "\\\\")));
        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty, "the extraction reported errors");
            Assert.That(table, Is.Not.Null, $"no table file holds the name; written: {string.Join(" || ", tables)}");
            Assert.That(table ?? "", Does.Contain("\"Name\": \"" + B(Column).Replace("\\", "\\\\")), "the column");
        });
    }

    private string Scalar(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_serverConnection);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void Run(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_serverConnection);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
