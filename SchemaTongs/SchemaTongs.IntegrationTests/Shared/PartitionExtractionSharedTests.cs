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

namespace SchemaTongs.IntegrationTests.Shared;

/// <summary>
/// What extraction reads outside the table JSON for a partitioned MySQL-family table: KEY partitioning's ALGORITHM, which
/// only SHOW CREATE TABLE prints, and subpartition names the table chose itself, which the package does not model.
/// </summary>
public abstract class PartitionExtractionSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }

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
        _sourceDb = $"partx_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            Run(cmd, $"CREATE DATABASE `{_sourceDb}`");
            conn.ChangeDatabase(_sourceDb);
            Run(cmd, "CREATE TABLE key_one (id INT NOT NULL PRIMARY KEY) PARTITION BY KEY ALGORITHM = 1 (id) PARTITIONS 2");
            Run(cmd, "CREATE TABLE key_two (id INT NOT NULL PRIMARY KEY) PARTITION BY KEY (id) PARTITIONS 2");
            Run(cmd, "CREATE TABLE sub_counted (id INT NOT NULL, y INT NOT NULL, PRIMARY KEY (id, y)) PARTITION BY RANGE (y) " +
                     "SUBPARTITION BY HASH (id) SUBPARTITIONS 2 (PARTITION p0 VALUES LESS THAN (2000), PARTITION p1 VALUES LESS THAN MAXVALUE)");
            Run(cmd, "CREATE TABLE sub_named (id INT NOT NULL, y INT NOT NULL, PRIMARY KEY (id, y)) PARTITION BY LIST (y) SUBPARTITION BY KEY (id) " +
                     "(PARTITION p0 VALUES IN (1) (SUBPARTITION a0, SUBPARTITION a1), PARTITION p1 VALUES IN (2) (SUBPARTITION b0, SUBPARTITION b1))");
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
    public void KeyAlgorithmOne_IsExtracted_AndTheDefaultIsNot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Written("key_one"), Does.Contain("\"KeyAlgorithm\": 1"), Written("key_one"));
            Assert.That(Written("key_two"), Does.Not.Contain("KeyAlgorithm"), Written("key_two"));
        });
    }

    [Test]
    public void SubpartitionsTheEngineNamed_Extract_AndNamedOnesAreRefusedByName()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Written("sub_counted"), Does.Contain("\"Subpartitioning\""), Written("sub_counted"));
            Assert.That(_written.Keys.Any(k => k.EndsWith("sub_named.json", StringComparison.OrdinalIgnoreCase)), Is.False, "the table is skipped");
            Assert.That(_errors, Has.One.Contains("sub_named").And.Contains("names its subpartitions"), string.Join(" | ", _errors));
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
