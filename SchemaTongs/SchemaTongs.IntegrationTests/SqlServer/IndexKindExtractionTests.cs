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
/// Index kinds a package cannot declare yet are left out of the table file, named in a warning, and counted as
/// skipped. Before, a spatial index was extracted as a plain nonclustered index over a geometry column, which the next
/// deploy refused, and a selective XML index was extracted as a primary XML index with no create statement, which the
/// deploy silently did not create. Asserted on a real extraction, as HistoryTableExtractionTests is.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class IndexKindExtractionTests
{
    private const string ProductName = "IndexKindProduct";
    private const string TemplateName = "Main";

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
        _db = $"TongsIdxKind_{Guid.NewGuid():N}"[..30];

        Master($"CREATE DATABASE [{_db}]");
        OnDb(cmd =>
        {
            Run(cmd, "CREATE TABLE dbo.Kinds (Id INT NOT NULL CONSTRAINT PK_Kinds PRIMARY KEY, Code INT NULL, g GEOMETRY NULL, x XML NULL, sx XML NULL)");
            Run(cmd, "CREATE INDEX IX_Kinds_Code ON dbo.Kinds (Code)");
            Run(cmd, "CREATE SPATIAL INDEX SX_Kinds ON dbo.Kinds (g) WITH (BOUNDING_BOX = (0, 0, 100, 100))");
            Run(cmd, "CREATE PRIMARY XML INDEX PX_Kinds ON dbo.Kinds (x)");
            Run(cmd, "CREATE XML INDEX XX_Kinds ON dbo.Kinds (x) USING XML INDEX PX_Kinds FOR PATH");
            Run(cmd, "CREATE SELECTIVE XML INDEX SXI_Kinds ON dbo.Kinds (sx) FOR (p1 = '/a/b')");
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            Master($"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Master($"DROP DATABASE IF EXISTS [{_db}]");
        }
        catch (DbException) { /* teardown must not mask an assertion */ }

        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    // The legacy encoding runs the XML generator, which reaches servers too old for xml_index_type.
    [TestCase(false)]
    [TestCase(true)]
    public void IndexKindsAPackageCannotDeclare_AreLeftOut_NamedAndCountedAsSkipped(bool legacyEncoding)
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsIdxKind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);
        var progressLog = Substitute.For<ILog>();
        global::SchemaTongs.SchemaTongs tongs;

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            var config = BuildConfig();
            if (legacyEncoding) config["Source:CompatEncoding"] = "legacy";
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);

            tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();
        }

        var tableFile = Path.Join(_tempProductPath, "Templates", TemplateName, "Tables", "dbo.Kinds.json");
        Assert.That(File.Exists(tableFile), Is.True, "the table itself must still be extracted");
        var json = File.ReadAllText(tableFile);
        var warnings = progressLog.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Warn")
            .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("IX_Kinds_Code").And.Contain("PX_Kinds").And.Contain("XX_Kinds"),
                "the kinds a package can declare are still extracted");
            Assert.That(json, Does.Not.Contain("SX_Kinds"), "a spatial index extracted as a plain index fails the next deploy");
            Assert.That(json, Does.Not.Contain("SXI_Kinds"), "a selective XML index extracted as a primary one is silently not created");
            Assert.That(warnings.Count(w => w.Contains("dbo.Kinds.SX_Kinds") && w.Contains("SPATIAL")), Is.EqualTo(1),
                "each index left out is named, with its kind");
            Assert.That(warnings.Count(w => w.Contains("dbo.Kinds.SXI_Kinds") && w.Contains("SELECTIVE")), Is.EqualTo(1));
            Assert.That(tongs.ExitCode, Is.EqualTo(1), "an object the package misses makes the run exit 1");
        });
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void Master(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, sql);
    }

    private void OnDb(Action<IDbCommand> act)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        conn.ChangeDatabase(_db);
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
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
