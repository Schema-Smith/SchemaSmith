// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Schema.Validation;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// Regression coverage for token-gated variant re-extraction (#270 / #343 pre-merge). When a table
/// carries same-named variants gated by a <b>ScriptToken</b> expression (e.g.
/// <c>'{{Edition}}'='Modern'</c>), re-extraction must substitute the package's configured
/// ScriptTokens into the gate before evaluating it against the source DB — exactly as deploy does.
///
/// <para>Before the fix, the gate was evaluated with the literal <c>{{Edition}}</c> placeholder, so
/// it never matched, no variant was considered active, and the extracted shape was written as a
/// spurious third <b>ungated</b> <c>Test.Widget.json</c> — which <c>--Validate</c> flags as
/// <c>SS-DUP-001</c>. With the fix, <c>{{Edition}}</c> resolves to <c>Modern</c>, the Modern variant
/// folds in place, the Legacy variant is left untouched, and no ungated duplicate is produced.</para>
/// </summary>
[Category("SqlServer")]
public class TokenGatedVariantReExtractionTests
{
    private const string TemplateName = "Main";
    private const string ProductName = "TokenGateProduct";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("TongsTokenGate");

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
    }

    [Test]
    public void ReExtract_TokenGatedVariant_FoldsActiveVariant_NoUngatedDuplicate()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsTokenGate_{Guid.NewGuid():N}");
        var tablesDir = Path.Join(_tempProductPath, "Templates", TemplateName, "Tables");
        Directory.CreateDirectory(tablesDir);

        // ----- pre-seed the package: Edition=Modern token, two token-gated variants -----
        SeedPackage(tablesDir);

        var modernFile = Path.Join(tablesDir, "Test.Widget.Modern.json");
        var legacyFile = Path.Join(tablesDir, "Test.Widget.Legacy.json");
        var bareFile = Path.Join(tablesDir, "Test.Widget.json");
        var legacyBefore = File.ReadAllText(legacyFile);

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
                // ----- re-extract from the Modern-deployed source DB -----
                var tongs = new SchemaTongs(Platform.SqlServer);
                tongs.CastTemplate();

                // The active (Modern) variant is refreshed in place: same file, gate + VariantName kept.
                Assert.That(File.Exists(modernFile), Is.True,
                    "Modern variant file must survive re-extraction (refreshed in place).");
                var modern = JsonHelper.TableLoad(modernFile, Platform.SqlServer);
                Assert.That(modern.VariantName, Is.EqualTo("Modern"),
                    "Refreshed active variant must retain its VariantName.");
                Assert.That(modern.ShouldApplyExpression, Is.EqualTo("'{{Edition}}'='Modern'"),
                    "Refreshed active variant must retain its (raw, authored) token gate.");

                // The inactive (Legacy) variant is untouched.
                Assert.That(File.Exists(legacyFile), Is.True, "Legacy variant file must be left in place.");
                Assert.That(File.ReadAllText(legacyFile), Is.EqualTo(legacyBefore),
                    "Inactive Legacy variant must be byte-for-byte untouched by re-extraction.");

                // No spurious ungated third entry — the token gate folded instead of falling through.
                Assert.That(File.Exists(bareFile), Is.False,
                    "Re-extraction must NOT write an ungated Test.Widget.json — the token gate should fold.");
                var widgetFiles = Directory.GetFiles(tablesDir, "Test.Widget*.json");
                Assert.That(widgetFiles.Length, Is.EqualTo(2),
                    "Exactly the two authored variant files must remain: " +
                    string.Join(", ", widgetFiles.Select(Path.GetFileName)));

                // ----- validate: the re-extracted package trips no SS-DUP-001 -----
                var validationConfig = new ConfigurationBuilder()
                    .AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string>("SchemaPackagePath", _tempProductPath)
                    })
                    .Build();
                FactoryContainer.Register<IConfigurationRoot>(validationConfig);

                var validator = new SchemaPackageValidator(PackageLoader.LoadPackage, ValidationCheckRegistry.Default());
                var result = validator.Validate(_tempProductPath);
                var dupFindings = result.Findings.Where(f => f.Code == "SS-DUP-001").ToList();

                Assert.That(dupFindings, Is.Empty,
                    "Token-gated variant re-extraction must not produce an ungated duplicate (SS-DUP-001): "
                    + string.Join("; ", dupFindings.Select(f => f.Message)));
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    // ----- setup helpers ---------------------------------------------------------------------

    private void SeedPackage(string tablesDir)
    {
        File.WriteAllText(Path.Join(_tempProductPath, "Product.json"),
            "{\n" +
            $"  \"Name\": \"{ProductName}\",\n" +
            "  \"ValidationScript\": \"SELECT 1\",\n" +
            "  \"TemplateOrder\": [ \"" + TemplateName + "\" ],\n" +
            "  \"ScriptTokens\": { \"Edition\": \"Modern\" },\n" +
            "  \"ScriptFolders\": [],\n" +
            "  \"Platform\": \"SqlServer\"\n" +
            "}\n");

        File.WriteAllText(Path.Join(_tempProductPath, "Templates", TemplateName, "Template.json"),
            "{\n" +
            "  \"Name\": \"" + TemplateName + "\",\n" +
            "  \"DatabaseIdentificationScript\": \"SELECT 1\",\n" +
            "  \"ScriptFolders\": []\n" +
            "}\n");

        // Two same-named (Test.Widget) variants, each gated by the Edition token.
        File.WriteAllText(Path.Join(tablesDir, "Test.Widget.Modern.json"),
            VariantTableJson("Modern", "'{{Edition}}'='Modern'",
                "{ \"Name\": \"[Id]\", \"DataType\": \"INT\" },\n" +
                "    { \"Name\": \"[Label]\", \"DataType\": \"NVARCHAR(50)\", \"Nullable\": true }"));

        File.WriteAllText(Path.Join(tablesDir, "Test.Widget.Legacy.json"),
            VariantTableJson("Legacy", "'{{Edition}}'='Legacy'",
                "{ \"Name\": \"[Id]\", \"DataType\": \"INT\" }"));
    }

    private static string VariantTableJson(string variantName, string gate, string columns) =>
        "{\n" +
        "  \"Name\": \"[Widget]\",\n" +
        "  \"Schema\": \"[Test]\",\n" +
        $"  \"VariantName\": \"{variantName}\",\n" +
        $"  \"ShouldApplyExpression\": \"{gate}\",\n" +
        "  \"Columns\": [\n    " + columns + "\n  ],\n" +
        "  \"Indexes\": [\n    { \"Name\": \"[PK_Widget]\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"[Id]\" }\n  ],\n" +
        "  \"ForeignKeys\": [],\n" +
        "  \"CheckConstraints\": []\n" +
        "}\n";

    private void CreateSourceDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Source DB carries the Modern shape of Test.Widget (the deployed edition).
        cmd.CommandText = @"
EXEC('CREATE SCHEMA [Test]');
CREATE TABLE Test.Widget (
    Id INT NOT NULL CONSTRAINT PK_Widget PRIMARY KEY,
    Label NVARCHAR(50) NULL
);";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _integrationDb);
        conn.Close();
    }

    private IConfigurationRoot BuildConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "SqlServer:ConnectionProperties");

        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["SqlServer:Server"],
            ["Source:Port"] = rootConfig["SqlServer:Port"],
            ["Source:User"] = rootConfig["SqlServer:User"],
            ["Source:Password"] = rootConfig["SqlServer:Password"],
            ["Source:Database"] = _integrationDb,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "false",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:UserDefinedTypes"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:Catalogs"] = "false",
            ["ShouldCast:StopLists"] = "false",
            ["ShouldCast:DDLTriggers"] = "false",
            ["ShouldCast:XMLSchemaCollections"] = "false",
            ["ShouldCast:IndexedViews"] = "false",
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
