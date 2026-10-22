// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.MariaDb;

/// <summary>
/// SYSTEM_TIME partitioning's INTERVAL, STARTS, LIMIT and AUTO are printed only by SHOW CREATE TABLE; under AUTO the
/// partition list is the engine's, so it is not written.
/// </summary>
[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class SystemTimeExtractionTests
{
    private static Platform Platform => Platform.MariaDb;
    private static string ConfigPrefix => "MariaDB";
    private bool _auto;

    private string _connectionString = "";
    private string _sourceDb = "";
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"], "mysql", config[$"{ConfigPrefix}:User"],
            config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"], ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"));
        _sourceDb = $"stx_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT VERSION()";
            if ((VersionHelper.ParsePatchComparable(cmd.ExecuteScalar()?.ToString(), Platform) ?? 0) < 100300)
                Assert.Ignore("System-versioned tables are MariaDB 10.3.");
            Run(cmd, $"CREATE DATABASE `{_sourceDb}`");
            conn.ChangeDatabase(_sourceDb);
            Run(cmd, "CREATE TABLE h_list (id INT NOT NULL PRIMARY KEY) WITH SYSTEM VERSIONING PARTITION BY SYSTEM_TIME LIMIT 1000 " +
                     "(PARTITION h1 HISTORY, PARTITION cur CURRENT)");
            try
            {
                Run(cmd, "CREATE TABLE h_auto (id INT NOT NULL PRIMARY KEY) WITH SYSTEM VERSIONING " +
                         "PARTITION BY SYSTEM_TIME INTERVAL 1 MONTH STARTS '2026-01-01 00:00:00' AUTO");
                _auto = true;
            }
            catch (MySqlConnector.MySqlException e) when (e.Number == 1064)
            {
                _auto = false; // AUTO is MariaDB 10.9.1
            }
        }
        Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"DROP DATABASE IF EXISTS `{_sourceDb}`");
    }

    [Test]
    public void ALimitTable_ExtractsItsLimitAndItsList()
    {
        var json = Written("h_list");
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Limit\": 1000"), json);
            Assert.That(json, Does.Contain("\"Values\": \"CURRENT\""), json);
            Assert.That(json, Does.Not.Contain("\"Expression\""), json);
        });
    }

    [Test]
    public void AnAutoTable_ExtractsItsOptions_AndNotTheEnginesList()
    {
        if (!_auto) Assert.Ignore("AUTO needs MariaDB 10.9.1.");
        var json = Written("h_auto");
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Interval\": \"1 MONTH\"").And.Contain("\"StartsAt\": \"2026-01-01 00:00:00\"").And.Contain("\"Auto\": true"), json);
            var partitioning = Newtonsoft.Json.Linq.JObject.Parse(json)["Partitioning"];
            Assert.That(partitioning?["Partitions"]?.HasValues ?? false, Is.False, "the engine's list is not the declaration: " + json);
            Assert.That(partitioning?["PartitionCount"], Is.Null, json);
        });
    }

    private string Written(string table) =>
        _written.FirstOrDefault(w => w.Key.EndsWith($"{table}.json", StringComparison.OrdinalIgnoreCase)).Value ?? "(not written)";

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Extract()
    {
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => _errors.Add(ci.ArgAt<object>(0)?.ToString()));
        var file = Substitute.For<IFile>();
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci => _written[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
            config["Source:User"] = config[$"{ConfigPrefix}:User"];
            config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            foreach (var kind in new[] { "Views", "Functions", "Procedures", "TableTriggers", "Events", "Sequences", "ValidateScripts" })
                config[$"ShouldCast:{kind}"] = "false";
            config["ShouldCast:Tables"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
