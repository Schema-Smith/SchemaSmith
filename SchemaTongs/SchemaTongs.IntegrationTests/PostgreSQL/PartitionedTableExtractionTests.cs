// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
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

namespace SchemaTongs.IntegrationTests.PostgreSQL;

// G1: a partitioned table was an ERROR and a skip, with every partition skipped beside it, so a database with one
// could not round-trip. It is extracted with its tree under Partitioning; a partition gets no file of its own; a trigger
// on the partitioned table is extracted and its clones on the partitions are not; what a partition has of its own and
// the package does not model is named in a warning.
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class PartitionedTableExtractionTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private string _productPath = "";
    private readonly List<string> _warnings = [];
    private bool _failed;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"],
            ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"PartSrc_{unique}";
        _productPath = Path.Join(Path.GetTempPath(), $"TongsPart_{unique}");
        Directory.CreateDirectory(_productPath);
        using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_sourceDb}\"";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(_sourceDb);
            cmd.CommandText = """
                CREATE TABLE public.events (id int NOT NULL, d date NOT NULL, PRIMARY KEY (id, d)) PARTITION BY RANGE (d);
                CREATE TABLE public.events_2026 PARTITION OF public.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
                CREATE INDEX ix_events_2026_local ON public.events_2026 (id);
                CREATE FUNCTION public.events_touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
                CREATE TRIGGER trg_events AFTER INSERT ON public.events FOR EACH ROW EXECUTE FUNCTION public.events_touch();
                """;
            cmd.ExecuteNonQuery();
        }
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig(config));
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            var progress = Substitute.For<ILog>();
            progress.When(l => l.Warn(Arg.Any<object>())).Do(c => _warnings.Add(c.Arg<object>()?.ToString() ?? ""));
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progress);
            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();
            _failed = tongs.Failed;
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

    private string Folder(string kind) => Path.Join(_productPath, "Templates", "Main", kind);

    [Test]
    public void ThePartitionedTable_IsExtractedWithItsTree_AndThePartitionIsNot()
    {
        var file = Directory.GetFiles(Folder("Tables")).SingleOrDefault(f => Path.GetFileName(f).Contains("events") && !Path.GetFileName(f).Contains("2026"));
        Assert.That(file, Is.Not.Null, "the partitioned table has a file");
        var partitioning = JObject.Parse(File.ReadAllText(file!))["Partitioning"];
        Assert.Multiple(() =>
        {
            Assert.That((string)partitioning?["Method"], Is.EqualTo("RANGE"));
            Assert.That((string)partitioning?["Partitions"]?[0]?["Name"], Is.EqualTo("events_2026"));
            Assert.That(Directory.GetFiles(Folder("Tables")).Where(f => Path.GetFileName(f).Contains("events_2026")), Is.Empty, "no file for the partition");
            Assert.That(_failed, Is.False, "extracting a partitioned table is not a failure");
        });
    }

    [Test]
    public void TheTriggerOnThePartitionedTable_IsExtracted_AndItsCloneIsNot()
    {
        var triggers = Directory.Exists(Folder("Triggers")) ? Directory.GetFiles(Folder("Triggers")).Select(Path.GetFileName).ToArray() : [];
        Assert.That(triggers, Is.EqualTo(new[] { "public.events.trg_events.sql" }));
    }

    [Test]
    public void AnIndexAPartitionHasOfItsOwn_IsNamedInAWarning()
    {
        Assert.That(_warnings, Has.Some.Contains("ix_events_2026_local"));
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
            ["Product:Name"] = "PartProduct",
            ["Template:Name"] = "Main",
        };
        foreach (var kind in new[] { "Schemas", "Functions", "CompositeTypes", "RangeTypes", "TextSearchConfigurations", "Operators", "Casts", "EventTriggers",
                                     "DomainTypes", "EnumTypes", "Collations", "Publications", "Sequences", "Views", "MaterializedViews", "Procedures", "Aggregates", "Rules" })
            values[$"ShouldCast:{kind}"] = "false";
        values["ShouldCast:Tables"] = "true";
        values["ShouldCast:TableTriggers"] = "true";
        foreach (var prop in ConnectionString.ReadProperties(root, "PostgreSQL:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
