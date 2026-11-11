// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
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
using Schema.Validation;

namespace SchemaTongs.IntegrationTests.Shared;

/// <summary>
/// The MySQL-family half of two extraction guards. With no <c>Product:ObjectOrder</c> a new extraction writes columns in
/// their physical order (alphabetical order made a fresh deploy reorder them, which breaks positional INSERTs). And
/// re-extracting into a hand-authored package refreshes it in place: the table file keeps its authored
/// <c>DataDelivery</c>, and the recycle-bin hook, which extraction never reads, is neither deleted nor dropped.
/// <para>Uses its own database: extraction reads every table in the source.</para>
/// </summary>
[NonParallelizable]
public abstract class ReExtractAndOrderSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string FixtureConnectionString { get; }

    private const string TemplateName = "Main";
    private const string ProductName = "ReExtractProductMy";

    private string _db = "";
    private readonly List<string> _productPaths = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"tongs_reextract_{Guid.NewGuid():N}"[..30];
        Exec($"CREATE DATABASE `{_db}`");
        Exec("CREATE TABLE ordered_t (zeta INT NOT NULL PRIMARY KEY, alpha VARCHAR(10) NULL, middle VARCHAR(10) NULL)", _db);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Exec($"DROP DATABASE IF EXISTS `{_db}`");
        foreach (var path in _productPaths.Where(Directory.Exists))
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void AnExtractionWithNoObjectOrder_WritesColumnsInPhysicalOrder()
    {
        var productPath = NewProductPath();
        Extract(productPath, cleanupMode: null);

        var file = Directory.GetFiles(Path.Join(productPath, "Templates", TemplateName, "Tables"), "*ordered_t*.json").Single();
        var table = JsonHelper.TableLoad(file, Platform);
        Assert.That(table.Columns.Select(c => c.Name.Trim('`')), Is.EqualTo(new[] { "zeta", "alpha", "middle" }),
            "with no ObjectOrder the columns must keep the table's physical order");
    }

    [Test]
    public void ReExtract_RefreshesADeclaredTableInPlace_AndLeavesTheHooksAlone()
    {
        var productPath = NewProductPath();
        var templateDir = Path.Join(productPath, "Templates", TemplateName);
        var tablesDir = Path.Join(templateDir, "Tables");
        var proceduresDir = Path.Join(templateDir, "Procedures");
        Directory.CreateDirectory(tablesDir);
        Directory.CreateDirectory(proceduresDir);
        File.WriteAllText(Path.Join(productPath, "Product.json"), $$"""
            { "Name": "{{ProductName}}", "ValidationScript": "SELECT 1", "TemplateOrder": [ "{{TemplateName}}" ],
              "ScriptTokens": {}, "ScriptFolders": [], "Platform": "{{Platform}}" }
            """);
        File.WriteAllText(Path.Join(templateDir, "Template.json"), $$"""
            { "Name": "{{TemplateName}}", "DatabaseIdentificationScript": "SELECT 1", "ScriptFolders": [] }
            """);
        // A stale column list, so a refresh can be told from an untouched file.
        var declaredFile = Path.Join(tablesDir, "ordered_t.json");
        File.WriteAllText(declaredFile, """
            {
              "Name": "ordered_t",
              "Columns": [ { "Name": "zeta", "DataType": "INT", "Nullable": false } ],
              "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "zeta" } ],
              "DataDelivery": [ { "ContentFile": "ordered_t.tabledata", "MergeType": "Insert/Update" } ]
            }
            """);
        var hookFile = Path.Join(proceduresDir, "SchemaSmith_CustomTableDrop.sql");
        File.WriteAllText(hookFile, "CREATE PROCEDURE SchemaSmith_CustomTableDrop(IN p_Table VARCHAR(64)) BEGIN END");

        Extract(productPath, cleanupMode: "DetectDeleteAndCleanup");

        var logsDir = Path.Join(templateDir, "Logs");
        var cleanupScripts = Directory.Exists(logsDir) ? Directory.GetFiles(logsDir, "_OrphanCleanup_*.sql").Select(File.ReadAllText).ToList() : [];
        Assert.That(Directory.GetFiles(tablesDir).Select(Path.GetFileName), Is.EquivalentTo(new[] { "ordered_t.json" }),
            "the declared file must be refreshed in place, with no duplicate beside it");
        var refreshed = JsonHelper.TableLoad(declaredFile, Platform);
        Assert.Multiple(() =>
        {
            Assert.That(refreshed.Columns.Select(c => c.Name.Trim('`')), Does.Contain("middle"), "the file carries the live table");
            Assert.That(refreshed.DataDelivery, Has.Count.EqualTo(1), "the authored DataDelivery survives the refresh");
            Assert.That(File.Exists(hookFile), Is.True, "the recycle-bin hook file is not deleted");
            Assert.That(cleanupScripts, Has.None.Contains("ordered_t").And.None.Contains("CustomTable"),
                "no cleanup DROP for a live table or for a hook: " + string.Join(" | ", cleanupScripts));
        });

        lock (FactoryContainer.SharedLockObject)
        {
            try
            {
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder()
                    .AddInMemoryCollection([new KeyValuePair<string, string>("SchemaPackagePath", productPath)]).Build());
                var findings = new SchemaPackageValidator(PackageLoader.LoadPackage, ValidationCheckRegistry.Default())
                    .Validate(productPath).Findings
                    .Where(f => f.Code is "SS-DUP-001" or "SS-FILE-NAME-003")
                    .ToList();
                Assert.That(findings, Is.Empty, string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}")));
            }
            finally
            {
                FactoryContainer.Clear();
            }
        }
    }

    private string NewProductPath()
    {
        var path = Path.Join(Path.GetTempPath(), $"TongsReExtractMy_{Guid.NewGuid():N}");
        _productPaths.Add(path);
        return path;
    }

    private void Extract(string productPath, string cleanupMode)
    {
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root[$"{ConfigPrefix}:Server"] ?? "127.0.0.1",
            ["Source:Port"] = root[$"{ConfigPrefix}:Port"],
            ["Source:User"] = root[$"{ConfigPrefix}:User"],
            ["Source:Password"] = root[$"{ConfigPrefix}:Password"],
            ["Source:Database"] = _db,
            ["Product:Path"] = productPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:ValidateScripts"] = "false"
        };
        if (cleanupMode != null) values["OrphanHandling:Mode"] = cleanupMode;
        foreach (var prop in ConnectionString.ReadProperties(root, $"{ConfigPrefix}:ConnectionProperties"))
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            try
            {
                new SchemaTongs(Platform).CastTemplate();
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    private void Exec(string sql, string database = null)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(FixtureConnectionString + "Database=information_schema;");
        conn.Open();
        if (database != null) conn.ChangeDatabase(database);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
