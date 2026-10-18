// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// A partitioned table names its partition scheme, and the scheme its function, but neither is modelled, so a package of
/// such a table failed on an empty database. SchemaTongs now writes them to a generated prerequisite script that runs
/// ahead of the tables and creates only what is missing.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class PartitionSchemeExtractionTests
{
    private const string ProductName = "PartSchemeProduct";
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
        _db = NewDatabase("TongsPartSchemes");
        OnDb(_db, cmd =>
        {
            Run(cmd, "CREATE PARTITION FUNCTION [pf_Id] (INT) AS RANGE RIGHT FOR VALUES (100, 200)");
            Run(cmd, "CREATE PARTITION SCHEME [ps_Id] AS PARTITION [pf_Id] ALL TO ([PRIMARY])");
            Run(cmd, "CREATE PARTITION FUNCTION [pf_O'Day] (DATETIME2(3)) AS RANGE LEFT FOR VALUES ('2026-01-01T00:00:00.123', '2026-07-01T12:30:00')");
            Run(cmd, "CREATE PARTITION SCHEME [ps_O'Day] AS PARTITION [pf_O'Day] TO ([PRIMARY], [PRIMARY], [PRIMARY])");
            Run(cmd, "CREATE PARTITION FUNCTION [pf_Code] (NVARCHAR(10)) AS RANGE RIGHT FOR VALUES (N'it''s', N'm')");
            Run(cmd, "CREATE PARTITION SCHEME [ps_Code] AS PARTITION [pf_Code] ALL TO ([PRIMARY])");
            Run(cmd, "CREATE PARTITION FUNCTION [pf_Unused] (INT) AS RANGE RIGHT FOR VALUES (1)");
            Run(cmd, "CREATE PARTITION SCHEME [ps_Unused] AS PARTITION [pf_Unused] ALL TO ([PRIMARY])");
            Run(cmd, "CREATE TABLE dbo.Orders (Id INT NOT NULL, Placed DATETIME2(3) NOT NULL, Code NVARCHAR(10) NOT NULL, " +
                     "CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (Id) ON [ps_Id](Id))");
            Run(cmd, "CREATE INDEX IX_Orders_Placed ON dbo.Orders (Placed) ON [ps_O'Day](Placed)");
            Run(cmd, "CREATE INDEX IX_Orders_Code ON dbo.Orders (Code) ON [ps_Code](Code)");
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
    public void TheSchemesAreWrittenToAPrerequisiteScript_AndTheExtractedTableDeploysToAnEmptyDatabase()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsPartSchemes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);
        Extract();

        var templateDir = Path.Join(_tempProductPath, "Templates", TemplateName);
        var scriptFile = Path.Join(templateDir, "Prerequisites", "PartitionSchemes.sql");
        Assert.That(File.Exists(scriptFile), Is.True, "the schemes are written to the prerequisite script");
        var script = File.ReadAllText(scriptFile);
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedScript.IsGenerated(script) && !GeneratedScript.IsHandEdited(script), Is.True, "stamped as generated");
            Assert.That(FirstFolder(templateDir), Is.EqualTo("Prerequisites:Objects"), "the template runs it first, in the pass ahead of the tables");
            Assert.That(script, Does.Not.Contain("pf_Unused"), "only what the tables and indexes are stored on");
        });

        // An empty database: the script creates the functions and schemes, a second run changes nothing, and the
        // extracted table deploys onto them.
        var target = NewDatabase("TongsPartTarget");
        OnDb(target, cmd =>
        {
            Run(cmd, script);
            Run(cmd, script);
            Assert.That(Layout(cmd), Is.EqualTo(string.Join(" | ", Layout(_db).Split(" | ").Where(x => !x.StartsWith("pf_Unused")))),
                "the same functions, boundaries and schemes as the source");

            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
            var table = File.ReadAllText(Path.Join(templateDir, "Tables", "dbo.Orders.json"));
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{ProductName}', @TableDefinitions = '[{table.Replace("'", "''")}]', @DropTablesRemovedFromProduct = 0";
            cmd.CommandTimeout = 300;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT STRING_AGG(CAST(i.[name] + '@' + ds.[name] AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY i.[name]) FROM sys.indexes i JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id WHERE i.[object_id] = OBJECT_ID('dbo.Orders')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("IX_Orders_Code@ps_Code,IX_Orders_Placed@ps_O'Day,PK_Orders@ps_Id"),
                "the table and its indexes land on their schemes");
        });
    }

    private void Extract()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }
    }

    private static string FirstFolder(string templateDir)
    {
        var first = JObject.Parse(File.ReadAllText(Path.Join(templateDir, "Template.json")))["ScriptFolders"]!.First();
        return $"{first["FolderPath"]}:{first["QuenchSlot"] ?? "Before"}";
    }

    private string Layout(string db)
    {
        var layout = "";
        OnDb(db, cmd => layout = Layout(cmd));
        return layout;
    }

    // Each function with its range side, type and boundaries, and the scheme over it with its filegroups.
    private static string Layout(IDbCommand cmd)
    {
        cmd.CommandText = """
            SELECT STRING_AGG(CAST(x AS NVARCHAR(MAX)), ' | ') WITHIN GROUP (ORDER BY x) FROM (
              SELECT x = pf.[name] + '/' + IIF(pf.boundary_value_on_right = 1, 'RIGHT', 'LEFT') + '/' + TYPE_NAME(pp.user_type_id) + '/'
                         + (SELECT STRING_AGG(CONVERT(NVARCHAR(100), rv.[value], 126), ',') WITHIN GROUP (ORDER BY rv.boundary_id)
                              FROM sys.partition_range_values rv WHERE rv.function_id = pf.function_id)
                         + ' > ' + ps.[name]
                         + (SELECT STRING_AGG('[' + fg.[name] + ']', '') WITHIN GROUP (ORDER BY dds.destination_id)
                              FROM sys.destination_data_spaces dds JOIN sys.filegroups fg ON fg.data_space_id = dds.data_space_id
                             WHERE dds.partition_scheme_id = ps.data_space_id)
                FROM sys.partition_functions pf
                JOIN sys.partition_parameters pp ON pp.function_id = pf.function_id
                JOIN sys.partition_schemes ps ON ps.function_id = pf.function_id) s
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
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
