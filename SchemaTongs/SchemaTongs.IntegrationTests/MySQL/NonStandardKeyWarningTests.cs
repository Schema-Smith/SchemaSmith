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

namespace SchemaTongs.IntegrationTests.MySQL;

/// <summary>
/// MySQL 8.0 deploys a foreign key to a non-unique key; 8.4 refuses it while restrict_fk_on_non_standard_key is on, its
/// default. Extraction says so by name, since the package it writes would stop deploying after an upgrade.
/// </summary>
[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class NonStandardKeyWarningTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private readonly List<string> _warnings = [];
    private bool _created;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.MySQL, config["MySQL:Server"], "mysql", config["MySQL:User"], config["MySQL:Password"], config["MySQL:Port"],
            ConnectionString.ReadProperties(config, "MySQL:ConnectionProperties"));
        _sourceDb = $"nskey_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _created = CreateSource();
        if (_created) Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"DROP DATABASE IF EXISTS `{_sourceDb}`");
    }

    [Test]
    public void AKeyToANonUniqueIndex_IsNamed_AndAKeyToTheUniqueOneIsNot()
    {
        if (!_created) Assert.Ignore("This server refuses a foreign key to a non-unique key (MySQL 8.4 default), so the source cannot be built.");
        var named = _warnings.Where(w => w.Contains("not a whole primary or unique key")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(named, Has.One.Contains("child.fk_child_code").And.Contains("parent (code)"), "warnings: " + string.Join(" | ", _warnings));
            Assert.That(named, Has.None.Contains("fk_child_id"));
        });
    }

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Extract()
    {
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => _warnings.Add(ci.ArgAt<object>(0)?.ToString()));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(Substitute.For<IFile>());
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["MySQL:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["MySQL:Port"];
            config["Source:User"] = config["MySQL:User"];
            config["Source:Password"] = config["MySQL:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "MySQL:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            foreach (var kind in new[] { "Views", "Functions", "Procedures", "TableTriggers", "Events", "Sequences", "ValidateScripts" })
                config[$"ShouldCast:{kind}"] = "false";
            config["ShouldCast:Tables"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform.MySQL).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private bool CreateSource()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"CREATE DATABASE `{_sourceDb}`");
        conn.ChangeDatabase(_sourceDb);
        Run(cmd, "CREATE TABLE parent (id INT NOT NULL PRIMARY KEY, code INT NOT NULL, KEY ix_code (code))");
        try
        {
            Run(cmd, "CREATE TABLE child (id INT NOT NULL PRIMARY KEY, parent_id INT NULL, parent_code INT NULL, " +
                     "CONSTRAINT fk_child_id FOREIGN KEY (parent_id) REFERENCES parent (id), " +
                     "CONSTRAINT fk_child_code FOREIGN KEY (parent_code) REFERENCES parent (code))");
            return true;
        }
        catch (Exception e) when (e.Message.Contains("Missing unique key"))
        {
            return false;
        }
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
