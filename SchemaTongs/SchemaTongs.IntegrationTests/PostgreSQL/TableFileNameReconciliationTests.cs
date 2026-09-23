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
using Schema.Validation.Checks;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// Regression coverage for the PostgreSQL extraction / <c>SS-FILE-NAME-003</c> reconciliation
/// (#270 / #343 pre-merge): SchemaTongs must name each extracted table file from the same schema
/// value it writes into the table content, so a package it just produced passes its own
/// <c>--Validate</c> table-file-name check.
///
/// <para>The default PostgreSQL schema (<c>public</c>) is omitted from content — deploy re-resolves
/// an unset Schema to <c>public</c> — so its file is schema-less (<c>widget.json</c>). A named
/// non-default schema is kept in content, so its file stays schema-qualified
/// (<c>sales.invoice.json</c>). Before the fix, a public table extracted as
/// <c>public.widget.json</c> with empty content Schema, which the check flagged because it derives
/// the canonical name from the (empty) content schema.</para>
/// </summary>
[Category("PostgreSQL")]
public class TableFileNameReconciliationTests
{
    private const string TemplateName = "Main";
    private const string ProductName = "ReconProduct";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        var server = config["PostgreSQL:Server"];
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, server, "postgres",
            config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("TongsFileName");

        CreateSourceDatabase();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch (IOException) { /* best effort — temp cleanup */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void Extraction_NamesTableFilesFromContentSchema_PassesTableFileNameCheck()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsFileNamePg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var config = BuildConfig();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register(environment);
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);

            try
            {
                // ----- extract (regular mode — no Source:Schema) -----
                var tongs = new SchemaTongs(Platform.PostgreSQL);
                tongs.CastTemplate();

                var tablesDir = Path.Join(_tempProductPath, "Templates", TemplateName, "Tables");
                var publicTableFile = Path.Join(tablesDir, "widget.json");
                var salesTableFile = Path.Join(tablesDir, "sales.invoice.json");

                // Default schema (public) is omitted → schema-less filename; NOT the old public.<t>.json.
                Assert.That(File.Exists(publicTableFile), Is.True,
                    "public-schema table must be emitted schema-less as widget.json.");
                Assert.That(File.Exists(Path.Join(tablesDir, "public.widget.json")), Is.False,
                    "public-schema table must NOT keep the public. prefix in its filename.");

                // Named non-default schema is kept → schema-qualified filename.
                Assert.That(File.Exists(salesTableFile), Is.True,
                    "sales-schema table must be emitted schema-qualified as sales.invoice.json.");

                // Content agrees with the filename: public omitted, sales retained.
                var publicSchema = (JsonHelper.TableLoad(publicTableFile, Platform.PostgreSQL)
                    as IDeliverableTable)?.Schema;
                Assert.That(string.IsNullOrEmpty(publicSchema), Is.True,
                    "public-schema table content must omit the default Schema (deploy re-resolves it).");

                var salesSchema = (JsonHelper.TableLoad(salesTableFile, Platform.PostgreSQL)
                    as IDeliverableTable)?.Schema;
                Assert.That(Identifier.Unwrap(salesSchema ?? "", Platform.PostgreSQL), Is.EqualTo("sales"),
                    "sales-schema table content must keep Schema=sales so it deploys to the right schema.");

                // ----- validate: the extraction output passes its own SS-FILE-NAME-003 check -----
                var ctx = new ValidationContext(
                    new Product { Name = ProductName, Platform = Platform.PostgreSQL },
                    new List<Template>(),
                    _tempProductPath);
                var fileNameFindings = new TableFileNameCheck().Run(ctx)
                    .Where(f => f.Code == "SS-FILE-NAME-003")
                    .ToList();

                Assert.That(fileNameFindings, Is.Empty,
                    "SchemaTongs' own extraction output must not trip SS-FILE-NAME-003: "
                    + string.Join("; ", fileNameFindings.Select(f => f.Message)));
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    // ----- setup helpers ---------------------------------------------------------------------

    private void CreateSourceDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        cmd.CommandText = @"
CREATE TABLE public.widget (
    widget_id INT NOT NULL CONSTRAINT pk_widget PRIMARY KEY,
    label VARCHAR(50) NULL
);

CREATE SCHEMA sales;
CREATE TABLE sales.invoice (
    invoice_id INT NOT NULL CONSTRAINT pk_invoice PRIMARY KEY,
    total NUMERIC(10, 2) NOT NULL
);";
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
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "false",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:DomainTypes"] = "false",
            ["ShouldCast:EnumTypes"] = "false",
            ["ShouldCast:CompositeTypes"] = "false",
            ["ShouldCast:Aggregates"] = "false",
            ["ShouldCast:Sequences"] = "false",
            ["ShouldCast:Rules"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:MaterializedViews"] = "false",
            ["ShouldCast:Schemas"] = "false",
            ["ShouldCast:ValidateScripts"] = "false"
        };
        foreach (var prop in connProps)
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string GenerateUniqueDBName(string prefix)
    {
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{prefix}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }
}
