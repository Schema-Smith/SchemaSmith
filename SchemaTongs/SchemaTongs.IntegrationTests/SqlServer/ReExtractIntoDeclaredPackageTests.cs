// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Schema.Validation;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// Re-extracting into a hand-authored package must refresh it, not break it: a table file that declares its schema
/// unbracketed and carries a <c>DataDelivery</c>, and a recycle-bin hook in SchemaSmith's own schema (which extraction
/// never reads), must come through with no duplicate table file, no deleted hook and no cleanup DROP for either.
/// <para>Uses its own database: extraction reads every table in the source.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class ReExtractIntoDeclaredPackageTests
{
    private const string TemplateName = "Main";
    private const string ProductName = "ReExtractProductSs";

    private string _db = "";
    private string _masterConnectionString = "";
    private string _tempProductPath;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = $"TongsReExtract_{Guid.NewGuid():N}"[..30];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE DATABASE [{_db}]";
        cmd.ExecuteNonQuery();
        conn.ChangeDatabase(_db);
        cmd.CommandText = "CREATE TABLE dbo.Categories (CategoryId INT NOT NULL CONSTRAINT PK_Categories PRIMARY KEY, Description NVARCHAR(100) NULL)";
        cmd.ExecuteNonQuery();
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
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _db);
        }
        catch (DbException) { /* teardown must not mask an assertion */ }
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch (IOException) { /* best effort - temp cleanup */ }
        }
    }

    [Test]
    public void ReExtract_RefreshesADeclaredTableInPlace_AndLeavesTheHooksAlone()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsReExtractSs_{Guid.NewGuid():N}");
        var templateDir = Path.Join(_tempProductPath, "Templates", TemplateName);
        var tablesDir = Path.Join(templateDir, "Tables");
        var proceduresDir = Path.Join(templateDir, "Procedures");
        var logsDir = Path.Join(templateDir, "Logs");
        Directory.CreateDirectory(tablesDir);
        Directory.CreateDirectory(proceduresDir);
        SeedPackage(tablesDir, proceduresDir);

        var declaredFile = Path.Join(tablesDir, "dbo.Categories.json");
        var hookFile = Path.Join(proceduresDir, "SchemaSmith.CustomTableDrop.sql");

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());

            try
            {
                new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();

                Assert.That(Directory.GetFiles(tablesDir).Select(Path.GetFileName), Is.EquivalentTo(new[] { "dbo.Categories.json" }),
                    "the declared file must be refreshed in place, with no duplicate beside it");
                var refreshed = JsonHelper.TableLoad(declaredFile, Platform.SqlServer);
                Assert.Multiple(() =>
                {
                    Assert.That(refreshed.Columns.Select(c => c.Name.Trim('[', ']')), Does.Contain("Description"),
                        "the file must carry the live table, not the stale seed");
                    Assert.That(refreshed.DataDelivery, Has.Count.EqualTo(1),
                        "the authored DataDelivery must survive the refresh");
                    Assert.That(File.Exists(hookFile), Is.True, "the recycle-bin hook file must not be deleted");
                });

                var cleanupScripts = Directory.Exists(logsDir)
                    ? Directory.GetFiles(logsDir, "_OrphanCleanup_*.sql").Select(File.ReadAllText).ToList()
                    : [];
                Assert.That(cleanupScripts, Has.None.Contains("Categories").And.None.Contains("CustomTable"),
                    "no cleanup DROP for a live table or for a hook: " + string.Join(" | ", cleanupScripts));

                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder()
                    .AddInMemoryCollection([new KeyValuePair<string, string>("SchemaPackagePath", _tempProductPath)])
                    .Build());
                var findings = new SchemaPackageValidator(PackageLoader.LoadPackage, ValidationCheckRegistry.Default())
                    .Validate(_tempProductPath).Findings
                    .Where(f => f.Code is "SS-DUP-001" or "SS-FILE-NAME-003")
                    .ToList();
                Assert.That(findings, Is.Empty, string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}")));
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    private void SeedPackage(string tablesDir, string proceduresDir)
    {
        File.WriteAllText(Path.Join(_tempProductPath, "Product.json"), $$"""
            {
              "Name": "{{ProductName}}",
              "ValidationScript": "SELECT 1",
              "TemplateOrder": [ "{{TemplateName}}" ],
              "ScriptTokens": {},
              "ScriptFolders": [],
              "Platform": "SqlServer"
            }
            """);
        File.WriteAllText(Path.Join(_tempProductPath, "Templates", TemplateName, "Template.json"), $$"""
            {
              "Name": "{{TemplateName}}",
              "DatabaseIdentificationScript": "SELECT 1",
              "ScriptFolders": []
            }
            """);

        // Hand-authored: the schema declared unbracketed, DataDelivery authored, and a stale column list so the test
        // can tell a refresh from an untouched file.
        File.WriteAllText(Path.Join(tablesDir, "dbo.Categories.json"), """
            {
              "Schema": "dbo",
              "Name": "Categories",
              "Columns": [ { "Name": "CategoryId", "DataType": "INT", "Nullable": false } ],
              "Indexes": [ { "Name": "PK_Categories", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "CategoryId" } ],
              "DataDelivery": [ { "ContentFile": "Categories.tabledata", "MergeType": "Insert/Update" } ]
            }
            """);

        File.WriteAllText(Path.Join(proceduresDir, "SchemaSmith.CustomTableDrop.sql"),
            "CREATE OR ALTER PROCEDURE [SchemaSmith].[CustomTableDrop] @p_Schema NVARCHAR(128), @p_Table NVARCHAR(128) AS RETURN;");
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
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["OrphanHandling:Mode"] = "DetectDeleteAndCleanup",
            ["ShouldCast:ValidateScripts"] = "false"
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
