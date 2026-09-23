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

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// Slice-6 schema-template round-trip integration test (design §7.6, plan Step 6.3) —
/// PostgreSQL mirror of the SQL Server fixture.
///
/// <para>The round-trip property: build a live PG source DB with a <c>tenant_seed</c>
/// schema (tables, FKs including one cross-schema to <c>public.countries</c>, a procedure
/// body referencing both <c>tenant_seed.customers</c> and <c>public.global_audit_log</c>,
/// and a view) → run SchemaTongs with <c>Source.Schema = "tenant_seed"</c> into a
/// temp directory → drop and recreate the <c>tenant_seed</c> schema empty → run SchemaQuench
/// against the extracted package (whose stub <c>SchemaIdentificationScript</c> returns
/// <c>'tenant_seed'</c>) → assert the rebuilt schema is structurally equivalent.</para>
///
/// <para>Materialized views are deferred — SchemaQuench has separate slice-3 coverage
/// for those (the PG mat-view tenancy regression). The high-signal assertions in this
/// round-trip cover table shape, both same-schema and cross-schema FK preservation, and
/// procedure-body cross-schema-reference preservation.</para>
/// </summary>
[Category("PostgreSQL")]
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
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _server = config["PostgreSQL:Server"];
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, _server, "postgres",
            config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
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
            $"SchemaTongsRoundTripPg_{Guid.NewGuid():N}");
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
                var tongs = new SchemaTongs(Platform.PostgreSQL);
                tongs.CastTemplate();

                // Sanity: package was produced.
                var productJsonPath = Path.Combine(_tempProductPath, "Product.json");
                Assert.That(File.Exists(productJsonPath), Is.True,
                    "Product.json must exist after SchemaTongs extraction.");
                var templatePath = Path.Combine(_tempProductPath, "Templates", TemplateName);
                Assert.That(File.Exists(Path.Combine(templatePath, "Template.json")), Is.True,
                    "Template.json must exist in the generated template folder.");
                var customersJson = Path.Combine(templatePath, "Tables", "customers.json");
                var ordersJson = Path.Combine(templatePath, "Tables", "orders.json");
                Assert.That(File.Exists(customersJson), Is.True,
                    "customers.json must be emitted with no schema prefix in the filename.");
                Assert.That(File.Exists(ordersJson), Is.True,
                    "orders.json must be emitted with no schema prefix in the filename.");

                // Cross-schema FK literal preserved in Orders.
                var ordersJsonContent = File.ReadAllText(ordersJson);
                Assert.That(ordersJsonContent, Does.Contain("public"),
                    "Cross-schema FK to public.countries must preserve the public literal in RelatedTableSchema.");

                // Procedure body collapses same-schema refs to {{SchemaName}} and preserves the
                // cross-schema public.global_audit_log reference (design §7.3). PG procedures live
                // under the Procedures folder.
                var procSqlPath = Path.Combine(templatePath, "Procedures", "get_customer_orders.sql");
                Assert.That(File.Exists(procSqlPath), Is.True,
                    "Procedure file must be emitted with no schema prefix in the filename.");
                var procBody = File.ReadAllText(procSqlPath);
                Assert.That(procBody, Does.Contain("{{SchemaName}}"),
                    "Procedure body must contain {{SchemaName}} after extraction (source-schema refs rewritten).");
                Assert.That(procBody, Does.Contain("public.global_audit_log")
                    .Or.Contain("public.\"global_audit_log\""),
                    "Procedure body must still reference public.global_audit_log (cross-schema literal preserved).");

                // ----- Step 2: drop and recreate the source schema empty -----
                DropAndRecreateTenantSchema();

                // ----- Step 3: re-deploy via SchemaQuench against the extracted package -----
                config["SchemaPackagePath"] = _tempProductPath;
                Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);

                progressLog.ClearReceivedCalls();

                SchemaQuench.Program.Main(["SkipKindlingForge"]);

                progressLog.DidNotReceive().Error(Arg.Any<string>());
                environment.DidNotReceive().Exit(2);
                environment.DidNotReceive().Exit(3);

                // ----- Step 4: assert structural equivalence -----
                AssertTableExists(SourceSchema, "customers");
                AssertTableExists(SourceSchema, "orders");

                // Customers columns survived.
                var customerCols = QueryRows(@$"
SELECT column_name || ':' || data_type
  FROM information_schema.columns
  WHERE table_schema = '{SourceSchema}' AND table_name = 'customers'
  ORDER BY ordinal_position");
                Assert.That(customerCols, Is.EquivalentTo(new[]
                {
                    "customer_id:integer",
                    "name:character varying"
                }), "customers column shape must match the original.");

                // Orders FKs: same-schema fk_orders_customers AND cross-schema fk_orders_countries.
                var sameIterationFk = ScalarCount(@$"
SELECT COUNT(*) FROM information_schema.table_constraints tc
  INNER JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
  WHERE tc.constraint_type = 'FOREIGN KEY'
    AND tc.table_schema = '{SourceSchema}' AND tc.table_name = 'orders'
    AND tc.constraint_name = 'fk_orders_customers'
    AND ccu.table_schema = '{SourceSchema}' AND ccu.table_name = 'customers'");
                Assert.That(sameIterationFk, Is.GreaterThanOrEqualTo(1),
                    "Same-iteration fk_orders_customers must be recreated pointing back to tenant_seed.customers.");

                var crossSchemaFk = ScalarCount(@$"
SELECT COUNT(*) FROM information_schema.table_constraints tc
  INNER JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
  WHERE tc.constraint_type = 'FOREIGN KEY'
    AND tc.table_schema = '{SourceSchema}' AND tc.table_name = 'orders'
    AND tc.constraint_name = 'fk_orders_countries'
    AND ccu.table_schema = 'public' AND ccu.table_name = 'countries'");
                Assert.That(crossSchemaFk, Is.GreaterThanOrEqualTo(1),
                    "Cross-schema fk_orders_countries must be recreated pointing at public.countries.");

                // Procedure body after iteration substitution must still reference both
                // tenant_seed.customers AND public.global_audit_log.
                var procDefinition = ScalarString(@$"
SELECT pg_get_functiondef(p.oid)
  FROM pg_proc p
  INNER JOIN pg_namespace n ON p.pronamespace = n.oid
  WHERE n.nspname = '{SourceSchema}' AND p.proname = 'get_customer_orders'");
                Assert.That(procDefinition, Is.Not.Null.And.Not.Empty,
                    "get_customer_orders procedure must be redeployed.");
                Assert.That(procDefinition, Does.Contain($"{SourceSchema}.customers")
                    .Or.Contain($"\"{SourceSchema}\".customers")
                    .Or.Contain($"\"{SourceSchema}\".\"customers\""),
                    "Procedure body must reference tenant_seed.customers after {{SchemaName}} resolves.");
                Assert.That(procDefinition, Does.Contain("public.global_audit_log")
                    .Or.Contain("public.\"global_audit_log\""),
                    "Procedure body must still reference public.global_audit_log (cross-schema literal preserved).");

                // View redeployed.
                var viewCount = ScalarCount(@$"
SELECT COUNT(*) FROM information_schema.views
  WHERE table_schema = '{SourceSchema}' AND table_name = 'customer_orders'");
                Assert.That(viewCount, Is.EqualTo(1),
                    "customer_orders view must be redeployed.");
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        // Cross-schema reference targets in public.
        cmd.CommandText = @"
CREATE TABLE public.countries (
    country_id INT NOT NULL CONSTRAINT pk_countries PRIMARY KEY,
    name VARCHAR(100) NOT NULL
);
CREATE TABLE public.global_audit_log (
    audit_id SERIAL CONSTRAINT pk_global_audit_log PRIMARY KEY,
    note VARCHAR(256) NOT NULL
);";
        cmd.ExecuteNonQuery();

        // Source schema with shape that exercises the round-trip property.
        cmd.CommandText = $@"
CREATE SCHEMA ""{SourceSchema}"";

CREATE TABLE ""{SourceSchema}"".customers (
    customer_id INT NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
    name VARCHAR(200) NOT NULL
);

CREATE TABLE ""{SourceSchema}"".orders (
    order_id INT NOT NULL CONSTRAINT pk_orders PRIMARY KEY,
    customer_id INT NOT NULL,
    country_id INT NULL,
    CONSTRAINT fk_orders_customers FOREIGN KEY (customer_id)
        REFERENCES ""{SourceSchema}"".customers (customer_id),
    CONSTRAINT fk_orders_countries FOREIGN KEY (country_id)
        REFERENCES public.countries (country_id)
);";
        cmd.ExecuteNonQuery();

        // View that references the source schema (will round-trip through {{SchemaName}}).
        cmd.CommandText = $@"
CREATE VIEW ""{SourceSchema}"".customer_orders
AS
SELECT c.customer_id, c.name, o.order_id
  FROM ""{SourceSchema}"".customers c
  INNER JOIN ""{SourceSchema}"".orders o ON o.customer_id = c.customer_id;";
        cmd.ExecuteNonQuery();

        // Procedure referencing BOTH source schema AND a cross-schema public object.
        cmd.CommandText = $@"
CREATE PROCEDURE ""{SourceSchema}"".get_customer_orders(p_customer_id INT)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO public.global_audit_log (note) VALUES ('get_customer_orders called');
    PERFORM c.customer_id, c.name, o.order_id
      FROM ""{SourceSchema}"".customers c
      INNER JOIN ""{SourceSchema}"".orders o ON o.customer_id = c.customer_id
      WHERE c.customer_id = p_customer_id;
END;
$$;";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void DropAndRecreateTenantSchema()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = $@"
DROP SCHEMA IF EXISTS ""{SourceSchema}"" CASCADE;
CREATE SCHEMA ""{SourceSchema}"";
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{ProductName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" = '{ProductName}';
    END IF;
END;
$$;";
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
            ["Target:Server"] = rootConfig["PostgreSQL:Server"],
            ["Target:Port"] = rootConfig["PostgreSQL:Port"],
            ["Target:User"] = rootConfig["PostgreSQL:User"],
            ["Target:Password"] = rootConfig["PostgreSQL:Password"],
            ["ScriptTokens:MainDB"] = _integrationDb,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["Source:Schema"] = SourceSchema,
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "true",
            ["ShouldCast:Procedures"] = "true",
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
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{tableName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected \"{schema}\".{tableName} to exist after re-deploy.");
    }

    private int ScalarCount(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
