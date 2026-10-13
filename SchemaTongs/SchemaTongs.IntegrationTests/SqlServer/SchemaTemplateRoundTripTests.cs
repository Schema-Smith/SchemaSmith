// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// Slice-6 schema-template round-trip integration test (design §7.6, plan Step 6.3).
///
/// <para>The round-trip property in concrete form: build a live source DB with a
/// <c>tenant_seed</c> schema (tables, FKs including one cross-schema to <c>dbo.Countries</c>,
/// a procedure body referencing both <c>tenant_seed.Customers</c> and a cross-schema
/// <c>dbo.GlobalAuditLog</c>, a view, and a check constraint) → run SchemaTongs with
/// <c>Source.Schema = "tenant_seed"</c> into a temp directory → drop and recreate
/// the <c>tenant_seed</c> schema empty → run SchemaQuench against the extracted package
/// (whose stub <c>SchemaIdentificationScript</c> returns <c>'tenant_seed'</c>) → assert
/// the rebuilt schema is structurally equivalent to the original.</para>
///
/// <para>Assertion scope is the highest-signal subset of design §7.6: tables exist with
/// the right columns, FKs preserved (same-schema and the cross-schema literal
/// <c>dbo</c> reference), procedure body still references both <c>tenant_seed.&lt;obj&gt;</c>
/// (via the {{SchemaName}} substitution that resolves back to <c>tenant_seed</c> at quench)
/// and the preserved cross-schema <c>dbo.GlobalAuditLog</c> literal. Indexed views are
/// deferred — SchemaQuench has separate slice-3 coverage for those (B5 regression).</para>
/// </summary>
[Category("SqlServer")]
public class SchemaTemplateRoundTripTests
{
    private const string SourceSchema = "tenant_seed";
    private const string TemplateName = "TenantBody";
    private const string ProductName = "RoundTripProduct";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;
    private string _server;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _server = config["SqlServer:Server"];
        _connectionString = ConnectionString.Build(Platform.SqlServer, _server, "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("TongsRoundTrip");

        CreateSourceDatabase();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch { /* best effort — temp cleanup */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void SchemaTongs_Extract_Then_SchemaQuench_Rebuild_Yields_Structurally_Equivalent_Schema()
    {
        _tempProductPath = Path.Combine(Path.GetTempPath(),
            $"SchemaTongsRoundTrip_{Guid.NewGuid():N}");
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
                // ----- Step 1: extract -----
                var tongs = new SchemaTongs(Platform.SqlServer);
                tongs.CastTemplate();

                // Sanity: the package was produced.
                var productJsonPath = Path.Combine(_tempProductPath, "Product.json");
                Assert.That(File.Exists(productJsonPath), Is.True,
                    "Product.json must exist after SchemaTongs extraction.");
                var templatePath = Path.Combine(_tempProductPath, "Templates", TemplateName);
                Assert.That(File.Exists(Path.Combine(templatePath, "Template.json")), Is.True,
                    "Template.json must exist in the generated template folder.");
                var customersJson = Path.Combine(templatePath, "Tables", "Customers.json");
                var ordersJson = Path.Combine(templatePath, "Tables", "Orders.json");
                Assert.That(File.Exists(customersJson), Is.True,
                    "Customers.json must be emitted with no schema prefix in the filename.");
                Assert.That(File.Exists(ordersJson), Is.True,
                    "Orders.json must be emitted with no schema prefix in the filename.");

                // The cross-schema FK literal must be preserved in the Orders.json (design §7.2).
                var ordersJsonContent = File.ReadAllText(ordersJson);
                Assert.That(ordersJsonContent, Does.Contain("dbo"),
                    "Cross-schema FK to dbo.Countries must preserve the [dbo] literal in RelatedTableSchema.");

                // The procedure body must collapse same-schema refs to {{SchemaName}} and preserve
                // the cross-schema dbo.GlobalAuditLog reference (design §7.3).
                var procSqlPath = Path.Combine(templatePath, "Procedures", "GetCustomerOrders.sql");
                Assert.That(File.Exists(procSqlPath), Is.True,
                    "Procedure file must be emitted with no schema prefix in the filename.");
                var procBody = File.ReadAllText(procSqlPath);
                Assert.That(procBody, Does.Contain("{{SchemaName}}"),
                    "Procedure body must contain {{SchemaName}} after extraction (source-schema refs rewritten).");
                Assert.That(procBody, Does.Contain("dbo"),
                    "Procedure body must still contain the cross-schema dbo.GlobalAuditLog reference.");
                Assert.That(procBody, Does.Not.Contain($"[{SourceSchema}]"),
                    "Procedure body must no longer contain the bracketed source-schema literal.");

                // #272: the mixed-bracket form [tenant_seed].Customers (bracketed schema, bare
                // object — what the SSMS view designer emits) must also rewrite the schema half
                // to {{SchemaName}}. Before the fix this survived un-rewritten and the package
                // failed to quench with "Invalid object name 'tenant_seed.Customers'".
                var mixedViewPath = Path.Combine(templatePath, "Views", "MixedRefView.sql");
                Assert.That(File.Exists(mixedViewPath), Is.True,
                    "MixedRefView.sql must be emitted with no schema prefix in the filename.");
                var mixedViewBody = File.ReadAllText(mixedViewPath);
                Assert.That(mixedViewBody, Does.Contain("{{SchemaName}}.Customers")
                    .Or.Contain("{{SchemaName}}.[Customers]"),
                    "Mixed-bracket [tenant_seed].Customers must rewrite the schema half to {{SchemaName}} (#272).");
                Assert.That(mixedViewBody, Does.Not.Contain($"[{SourceSchema}].Customers"),
                    "The bracketed-schema / bare-object source reference must not survive extraction (#272).");

                // ----- Step 2: drop and recreate the source schema empty -----
                DropAndRecreateTenantSchema();

                // ----- Step 3: re-deploy via SchemaQuench against the extracted package -----
                // Switch from extraction (Source:*) to quench (Target:*) by re-pointing
                // SchemaPackagePath. The same registered IConfigurationRoot serves both phases.
                config["SchemaPackagePath"] = _tempProductPath;
                config["SchemaSmith.CompletedMigrationScripts:Path"] = ""; // safety
                Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);

                // Reset received-calls on the progress log so we can scope log assertions to the
                // re-deploy phase.
                progressLog.ClearReceivedCalls();

                SchemaQuench.Program.Main(["SkipKindlingForge"]);

                // The schema must not have errored.
                progressLog.DidNotReceive().Error(Arg.Any<string>());
                environment.DidNotReceive().Exit(2);
                environment.DidNotReceive().Exit(3);

                // ----- Step 4: assert structural equivalence -----
                AssertTableExists(SourceSchema, "Customers");
                AssertTableExists(SourceSchema, "Orders");

                // Customer columns survived.
                var customerCols = QueryRows(@$"
SELECT c.name + ':' + ty.name
  FROM sys.columns c
  INNER JOIN sys.tables t ON c.object_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
  WHERE s.name = '{SourceSchema}' AND t.name = 'Customers'
  ORDER BY c.column_id");
                Assert.That(customerCols, Is.EquivalentTo(new[]
                {
                    "CustomerID:int",
                    "Name:nvarchar"
                }), "Customers column shape must match the original.");

                // Orders FKs: same-schema FK_Orders_Customers AND cross-schema FK_Orders_Countries.
                var sameIterationFk = ScalarCount(@$"
SELECT COUNT(*) FROM sys.foreign_keys fk
  INNER JOIN sys.tables ft ON fk.parent_object_id = ft.object_id
  INNER JOIN sys.schemas fs ON ft.schema_id = fs.schema_id
  INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
  INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
WHERE fs.name = '{SourceSchema}' AND ft.name = 'Orders' AND fk.name = 'FK_Orders_Customers'
  AND rs.name = '{SourceSchema}' AND rt.name = 'Customers'");
                Assert.That(sameIterationFk, Is.EqualTo(1),
                    "Same-iteration FK FK_Orders_Customers must be recreated pointing back to tenant_seed.Customers.");

                var crossSchemaFk = ScalarCount(@$"
SELECT COUNT(*) FROM sys.foreign_keys fk
  INNER JOIN sys.tables ft ON fk.parent_object_id = ft.object_id
  INNER JOIN sys.schemas fs ON ft.schema_id = fs.schema_id
  INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
  INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
WHERE fs.name = '{SourceSchema}' AND ft.name = 'Orders' AND fk.name = 'FK_Orders_Countries'
  AND rs.name = 'dbo' AND rt.name = 'Countries'");
                Assert.That(crossSchemaFk, Is.EqualTo(1),
                    "Cross-schema FK FK_Orders_Countries must be recreated pointing at dbo.Countries.");

                // Procedure body: after the engine substitutes {{SchemaName}} back to tenant_seed,
                // we should see references to both tenant_seed.Customers AND dbo.GlobalAuditLog.
                var procDefinition = ScalarString(@$"
SELECT m.definition
  FROM sys.sql_modules m
  INNER JOIN sys.objects o ON m.object_id = o.object_id
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{SourceSchema}' AND o.name = 'GetCustomerOrders'");
                Assert.That(procDefinition, Is.Not.Null.And.Not.Empty,
                    "GetCustomerOrders procedure must be redeployed.");
                // The engine emits `<schema>.[Customers]` after the {{SchemaName}} token resolves —
                // schema half is bare, object half stays bracketed. Match the substring without
                // pinning the bracket style on the schema half.
                Assert.That(procDefinition, Does.Contain($"{SourceSchema}.[Customers]")
                    .Or.Contain($"[{SourceSchema}].[Customers]")
                    .Or.Contain($"{SourceSchema}.Customers"),
                    "Procedure body must reference tenant_seed.Customers after {{SchemaName}} resolves.");
                Assert.That(procDefinition, Does.Contain("[dbo].[GlobalAuditLog]")
                    .Or.Contain("dbo.[GlobalAuditLog]")
                    .Or.Contain("dbo.GlobalAuditLog"),
                    "Procedure body must still reference dbo.GlobalAuditLog (cross-schema literal preserved).");

                // View redeployed.
                var viewDefinition = ScalarString(@$"
SELECT m.definition
  FROM sys.sql_modules m
  INNER JOIN sys.views v ON m.object_id = v.object_id
  INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
  WHERE s.name = '{SourceSchema}' AND v.name = 'CustomerOrders'");
                Assert.That(viewDefinition, Is.Not.Null.And.Not.Empty,
                    "CustomerOrders view must be redeployed.");
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    // ----- Setup helpers ---------------------------------------------------------------------

    private void CreateSourceDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Cross-schema reference targets in dbo.
        cmd.CommandText = @"
CREATE TABLE dbo.Countries (
    [CountryID] INT NOT NULL CONSTRAINT [PK_Countries] PRIMARY KEY,
    [Name] NVARCHAR(100) NOT NULL
);
CREATE TABLE dbo.GlobalAuditLog (
    [AuditID] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_GlobalAuditLog] PRIMARY KEY,
    [Note] NVARCHAR(256) NOT NULL
);";
        cmd.ExecuteNonQuery();

        // Source schema with shape that exercises the round-trip property.
        cmd.CommandText = $@"
EXEC('CREATE SCHEMA [{SourceSchema}]');

CREATE TABLE [{SourceSchema}].[Customers] (
    [CustomerID] INT NOT NULL CONSTRAINT [PK_Customers] PRIMARY KEY,
    [Name] NVARCHAR(200) NOT NULL
);

CREATE TABLE [{SourceSchema}].[Orders] (
    [OrderID] INT NOT NULL CONSTRAINT [PK_Orders] PRIMARY KEY,
    [CustomerID] INT NOT NULL,
    [CountryID] INT NULL,
    CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerID])
        REFERENCES [{SourceSchema}].[Customers] ([CustomerID]),
    CONSTRAINT [FK_Orders_Countries] FOREIGN KEY ([CountryID])
        REFERENCES [dbo].[Countries] ([CountryID])
);";
        cmd.ExecuteNonQuery();

        // View that references the source schema (will round-trip through {{SchemaName}}).
        cmd.CommandText = $@"
CREATE VIEW [{SourceSchema}].[CustomerOrders]
AS
SELECT c.[CustomerID], c.[Name], o.[OrderID]
  FROM [{SourceSchema}].[Customers] c
  INNER JOIN [{SourceSchema}].[Orders] o ON o.[CustomerID] = c.[CustomerID];";
        cmd.ExecuteNonQuery();

        // #272: a view using the SSMS view-designer MIXED-bracket form — bracketed schema,
        // BARE object ([tenant_seed].Customers) — which the old same-wrapper rewriter missed.
        cmd.CommandText = $@"
CREATE VIEW [{SourceSchema}].[MixedRefView]
AS
SELECT c.[CustomerID], c.[Name]
  FROM [{SourceSchema}].Customers c;";
        cmd.ExecuteNonQuery();

        // Procedure referencing BOTH source schema AND a cross-schema dbo object.
        cmd.CommandText = $@"
CREATE PROCEDURE [{SourceSchema}].[GetCustomerOrders] @customerId INT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[GlobalAuditLog] ([Note]) VALUES ('GetCustomerOrders called');
    SELECT c.[CustomerID], c.[Name], o.[OrderID]
      FROM [{SourceSchema}].[Customers] c
      INNER JOIN [{SourceSchema}].[Orders] o ON o.[CustomerID] = c.[CustomerID]
      WHERE c.[CustomerID] = @customerId;
END;";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void DropAndRecreateTenantSchema()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        // Drop everything in the tenant_seed schema in dependency-safe order: FKs first
        // (including cross-schema inbound FKs would matter, but our schema has no inbound FKs),
        // then procs, views, tables, then the schema. Also clear ProductOwnership / migration
        // tracking so the second deploy treats the schema as virgin (parity with SchemaQuench
        // round-trip tests).
        cmd.CommandText = $@"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + 'ALTER TABLE [' + s.name + '].[' + t.name + '] DROP CONSTRAINT [' + fk.name + '];' + CHAR(10)
  FROM sys.foreign_keys fk
  INNER JOIN sys.tables t ON fk.parent_object_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{SourceSchema}';
SELECT @sql = @sql + 'DROP PROCEDURE [' + s.name + '].[' + o.name + '];' + CHAR(10)
  FROM sys.objects o
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{SourceSchema}' AND o.type = 'P';
SELECT @sql = @sql + 'DROP VIEW [' + s.name + '].[' + v.name + '];' + CHAR(10)
  FROM sys.views v
  INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
  WHERE s.name = '{SourceSchema}';
SELECT @sql = @sql + 'DROP TABLE [' + s.name + '].[' + t.name + '];' + CHAR(10)
  FROM sys.tables t
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{SourceSchema}';
IF @sql <> '' EXEC sp_executesql @sql;
IF SCHEMA_ID('{SourceSchema}') IS NOT NULL EXEC('DROP SCHEMA [{SourceSchema}]');
EXEC('CREATE SCHEMA [{SourceSchema}]');
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}';
IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '{ProductName}';";
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

        // Start from an empty in-memory config so the SchemaTongs and SchemaQuench passes do
        // not pick up settings from prior fixtures in the same process. Map SqlServer:* values
        // explicitly onto Source:* and Target:* so both tools find what they need.
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["SqlServer:Server"],
            ["Source:Port"] = rootConfig["SqlServer:Port"],
            ["Source:User"] = rootConfig["SqlServer:User"],
            ["Source:Password"] = rootConfig["SqlServer:Password"],
            ["Source:Database"] = _integrationDb,
            ["Target:Server"] = rootConfig["SqlServer:Server"],
            ["Target:Port"] = rootConfig["SqlServer:Port"],
            ["Target:User"] = rootConfig["SqlServer:User"],
            ["Target:Password"] = rootConfig["SqlServer:Password"],
            ["ScriptTokens:MainDB"] = _integrationDb,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["Source:Schema"] = SourceSchema,
            // Cast everything that the round-trip needs, suppress what's irrelevant.
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "true",
            ["ShouldCast:Procedures"] = "true",
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
        {
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string GenerateUniqueDBName(string prefix)
    {
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{prefix}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    // ----- Assertion helpers -----------------------------------------------------------------

    private void AssertTableExists(string schema, string tableName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{schema}' AND t.name = '{tableName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected [{schema}].[{tableName}] to exist after re-deploy.");
    }

    private int ScalarCount(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result);
    }

    private string ScalarString(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        conn.Close();
        return result?.ToString();
    }

    private List<string> QueryRows(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader[0]?.ToString());
        conn.Close();
        return rows;
    }
}
