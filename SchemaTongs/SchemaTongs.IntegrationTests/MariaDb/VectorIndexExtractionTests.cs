// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.MariaDb;

/// <summary>
/// A VECTOR index (MariaDB 11.7) is extracted as one, with the M and DISTANCE it was declared with; an index declared
/// without them is extracted without them, so a redeploy keeps the server defaults.
/// </summary>
[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class VectorIndexExtractionTests
{
    private static Platform Platform => Platform.MariaDb;
    private static string ConfigPrefix => "MariaDB";
    private bool _supported;

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
        _sourceDb = $"vix_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            Run(cmd, $"CREATE DATABASE `{_sourceDb}`");
            conn.ChangeDatabase(_sourceDb);
            try
            {
                Run(cmd, "CREATE TABLE v_opts (id INT NOT NULL PRIMARY KEY, v VECTOR(3) NOT NULL, " +
                         "VECTOR INDEX vx (v) COMMENT 'M=99 (not an option)' M=8 DISTANCE=cosine)");
                Run(cmd, "CREATE TABLE v_defaults (id INT NOT NULL PRIMARY KEY, v VECTOR(3) NOT NULL, VECTOR INDEX vd (v))");
                _supported = true;
            }
            catch (MySqlConnector.MySqlException)
            {
                _supported = false;
            }
        }
        if (_supported) Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"DROP DATABASE IF EXISTS `{_sourceDb}`");
    }

    private JToken Index(string table, string name)
    {
        if (!_supported) Assert.Ignore("Vector indexes arrived in MariaDB 11.7.");
        var json = Written(table);
        Assert.That(json, Is.Not.EqualTo("(not written)"), string.Join("; ", _errors));
        return JObject.Parse(json)["Indexes"]?.FirstOrDefault(i => (string)i["Name"] == $"`{name}`" || (string)i["Name"] == name)
               ?? throw new AssertionException($"no index {name}: {json}");
    }

    [Test]
    public void AVectorIndex_IsExtractedAsOne_WithTheOptionsItWasDeclaredWith()
    {
        var index = Index("v_opts", "vx");
        Assert.Multiple(() =>
        {
            Assert.That((string)index["IndexType"], Is.EqualTo("VECTOR").IgnoreCase, index.ToString());
            Assert.That((int?)index["M"], Is.EqualTo(8), index.ToString());
            Assert.That((string)index["Distance"], Is.EqualTo("cosine"), index.ToString());
        });
    }

    [Test]
    public void AVectorIndexDeclaredWithoutOptions_IsExtractedWithoutThem()
    {
        var index = Index("v_defaults", "vd");
        Assert.Multiple(() =>
        {
            Assert.That((string)index["IndexType"], Is.EqualTo("VECTOR").IgnoreCase, index.ToString());
            Assert.That(index["M"], Is.Null, index.ToString());
            Assert.That(index["Distance"], Is.Null, index.ToString());
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
