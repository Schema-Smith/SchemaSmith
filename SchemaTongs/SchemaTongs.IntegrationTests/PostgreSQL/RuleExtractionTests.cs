// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

// B27: a rule was scripted by replacing every "CREATE RULE" in its definition, so a rule whose action wrote that text
// was extracted with the user's own string rewritten.
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class RuleExtractionTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private string _productPath = "";

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"],
            ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"RuleSrc_{unique}";
        _productPath = Path.Join(Path.GetTempPath(), $"TongsRules_{unique}");
        Directory.CreateDirectory(_productPath);

        using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_sourceDb}\"";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(_sourceDb);
            cmd.CommandText = """
                CREATE TABLE public.items (id int PRIMARY KEY);
                CREATE TABLE public.notes (msg text);
                CREATE RULE log_items AS ON INSERT TO public.items DO ALSO INSERT INTO public.notes VALUES ('CREATE RULE fired');
                """;
            cmd.ExecuteNonQuery();
        }

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig(config));
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            new SchemaTongs(Platform.PostgreSQL).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_sourceDb}\" WITH (FORCE)";
            cmd.ExecuteNonQuery();
        }
        catch (System.Data.Common.DbException) { /* best-effort cleanup must not mask a test result */ }
        try { Directory.Delete(_productPath, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Test]
    public void ARuleWritingTheTextCreateRule_IsExtractedUnchanged()
    {
        var file = Path.Join(_productPath, "Templates", "Main", "Rules", "public.items.log_items.sql");
        Assert.That(File.Exists(file), Is.True, $"the rule was extracted to {file}");
        var script = File.ReadAllText(file);
        Assert.That(script, Does.StartWith("CREATE OR REPLACE RULE"), script);
        Assert.That(script, Does.Contain("'CREATE RULE fired'"), script);
    }

    private IConfigurationRoot BuildConfig(IConfigurationRoot root)
    {
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root["PostgreSQL:Server"],
            ["Source:Port"] = root["PostgreSQL:Port"],
            ["Source:User"] = root["PostgreSQL:User"],
            ["Source:Password"] = root["PostgreSQL:Password"],
            ["Source:Database"] = _sourceDb,
            ["Target:Server"] = root["PostgreSQL:Server"],
            ["Target:Port"] = root["PostgreSQL:Port"],
            ["Target:User"] = root["PostgreSQL:User"],
            ["Target:Password"] = root["PostgreSQL:Password"],
            ["ScriptTokens:MainDb"] = _sourceDb,
            ["Product:Path"] = _productPath,
            ["Product:Name"] = "RuleProduct",
            ["Template:Name"] = "Main",
        };
        foreach (var kind in new[] { "Tables", "Schemas", "Functions", "CompositeTypes", "TableTriggers", "RangeTypes", "TextSearchConfigurations", "Operators", "Casts", "EventTriggers",
                                     "DomainTypes", "EnumTypes", "Collations", "Publications", "Sequences", "Views", "MaterializedViews", "Procedures", "Aggregates" })
            values[$"ShouldCast:{kind}"] = "false";
        values["ShouldCast:Rules"] = "true";
        foreach (var prop in ConnectionString.ReadProperties(root, "PostgreSQL:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
