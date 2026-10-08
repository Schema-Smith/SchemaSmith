// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Schema.Validation;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// Re-extracting into a package shaped like the shipped PostgreSQL demos must refresh it, not break it. Those packages
/// declare <c>"Schema": "public"</c> (which extraction omits) and carry the recycle-bin hooks in SchemaSmith's own
/// namespace (which extraction never reads). Before the fix a re-extract wrote a bare duplicate of every table without
/// its <c>DataDelivery</c>, reported the originals as orphans, and the cleanup modes deleted the originals and the hook
/// files and scripted DROPs for every live table and both hooks.
/// <para>Uses its own database: extraction reads every table in the source, and the package must hold exactly the
/// tables seeded here.</para>
/// </summary>
[Category("PostgreSQL")]
public class ReExtractIntoDeclaredPackageTests
{
    private const string TemplateName = "Main";
    private const string ProductName = "ReExtractProduct";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres",
            config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = $"tongs_reextract_{Guid.NewGuid():N}"[..30];
        CreateSourceDatabase();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch (IOException) { /* best effort - temp cleanup */ }
        }
    }

    [Test]
    public void ReExtract_RefreshesDeclaredPublicTablesInPlace_AndLeavesTheHooksAlone()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsReExtract_{Guid.NewGuid():N}");
        var templateDir = Path.Join(_tempProductPath, "Templates", TemplateName);
        var tablesDir = Path.Join(templateDir, "Tables");
        var proceduresDir = Path.Join(templateDir, "Procedures");
        var logsDir = Path.Join(templateDir, "Logs");
        Directory.CreateDirectory(tablesDir);
        Directory.CreateDirectory(proceduresDir);
        SeedPackage(tablesDir, proceduresDir);

        var declaredFile = Path.Join(tablesDir, "public.categories.json");
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
                new SchemaTongs(Platform.PostgreSQL).CastTemplate();

                Assert.That(Directory.GetFiles(tablesDir).Select(Path.GetFileName), Is.EquivalentTo(new[] { "public.categories.json" }),
                    "the declared file must be refreshed in place, with no bare duplicate beside it");
                var refreshed = JsonHelper.TableLoad(declaredFile, Platform.PostgreSQL);
                Assert.Multiple(() =>
                {
                    Assert.That(((IDeliverableTable)refreshed).Schema, Is.EqualTo("public"),
                        "the refreshed file keeps the form it was written in");
                    Assert.That(refreshed.Columns.Select(c => c.Name.Trim('"')), Does.Contain("description"),
                        "the file must carry the live table, not the stale seed");
                    Assert.That(refreshed.DataDelivery, Has.Count.EqualTo(1),
                        "the authored DataDelivery must survive the refresh");
                    Assert.That(File.Exists(hookFile), Is.True, "the recycle-bin hook file must not be deleted");
                });

                var cleanupScripts = Directory.Exists(logsDir)
                    ? Directory.GetFiles(logsDir, "_OrphanCleanup_*.sql").Select(File.ReadAllText).ToList()
                    : [];
                Assert.That(cleanupScripts, Has.None.Contains("categories").And.None.Contains("CustomTable"),
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
              "Platform": "PostgreSQL"
            }
            """);
        File.WriteAllText(Path.Join(_tempProductPath, "Templates", TemplateName, "Template.json"), $$"""
            {
              "Name": "{{TemplateName}}",
              "DatabaseIdentificationScript": "SELECT 1",
              "ScriptFolders": []
            }
            """);

        // The demo shape: Schema declared, DataDelivery authored. The column list is deliberately stale so the
        // test can tell a refresh from an untouched file.
        File.WriteAllText(Path.Join(tablesDir, "public.categories.json"), """
            {
              "Schema": "public",
              "Name": "categories",
              "Columns": [ { "Name": "category_id", "DataType": "INT4", "Nullable": false } ],
              "Indexes": [ { "Name": "pk_categories", "PrimaryKey": true, "Unique": true, "IndexColumns": "category_id" } ],
              "DataDelivery": [ { "ContentFile": "categories.tabledata", "MergeType": "Insert/Update" } ]
            }
            """);

        File.WriteAllText(Path.Join(proceduresDir, "SchemaSmith.CustomTableDrop.sql"),
            "CREATE OR REPLACE PROCEDURE \"SchemaSmith\".\"CustomTableDrop\"(p_Schema TEXT, p_Table TEXT) LANGUAGE plpgsql AS $$ BEGIN END $$;");
    }

    private void CreateSourceDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
        cmd.CommandText = "CREATE TABLE public.categories (category_id INT4 NOT NULL CONSTRAINT pk_categories PRIMARY KEY, description TEXT NULL);";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private IConfigurationRoot BuildConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "PostgreSQL:ConnectionProperties");
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["PostgreSQL:Server"],
            ["Source:Port"] = rootConfig["PostgreSQL:Port"],
            ["Source:User"] = rootConfig["PostgreSQL:User"],
            ["Source:Password"] = rootConfig["PostgreSQL:Password"],
            ["Source:Database"] = _integrationDb,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["OrphanHandling:Mode"] = "DetectDeleteAndCleanup",
            ["ShouldCast:ValidateScripts"] = "false"
        };
        foreach (var prop in connProps)
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
