// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Slice-8 end-to-end integration validator for the Schema Templates feature, deploying the
/// public-facing <c>Demos/PostgreSQL/TenantCRM</c> demo product against a live PostgreSQL.
/// The demo is what real customers will study; if this test passes, the feature delivers
/// what the demo claims it does.
///
/// <para>Test walks the full lifecycle exactly the way the README walks the user through it:
/// (1) fresh quench against an empty <c>public.tenants</c> deploys the Shared template only;
/// (2) <c>public.onboard_tenant</c> onboards three tenants; (3) re-quench iterates per tenant;
/// (4) per-tenant structure / FKs / migration tracking / procedures verified; (5) onboard a
/// fourth tenant; (6) selective quench with <c>Target.Schemas</c> deploys only the fourth
/// tenant; (7) original three tenants' state untouched.</para>
/// </summary>
[Category("PostgreSQL")]
public class TenantCRMEndToEndTests
{
    private const string ProductName = "TenantCRM";
    private const string TenantWorkspaceTemplate = "TenantWorkspace";

    private static readonly string[] InitialTenants = ["tenant_acme", "tenant_beta", "tenant_globex"];
    private const string FourthTenant = "tenant_fourth";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public TenantCRMEndToEndTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    /// <summary>
    /// Regression guard for parallel schema-template fan-out (PG). Many tenant schemas deployed
    /// concurrently must all converge cleanly. This previously deadlocked inside
    /// <c>MissingIndexesAndConstraintsQuench</c>: a correlated EXISTS over
    /// <c>information_schema.columns</c> materialised the whole view and AccessShare-locked every
    /// table, colliding with siblings creating their own tables ("40P01: deadlock detected"). The
    /// proc now reads <c>pg_catalog</c> scoped to the named table, preventing the deadlock at the
    /// source; the <c>DatabaseQuench</c> retry path remains as defense-in-depth. High tenant count
    /// makes the old race reliable, so this guards against a regression of either layer (the
    /// 3-tenant lifecycle test rarely trips it).
    /// </summary>
    [Test]
    public void Parallel_FanOut_ManyTenants_DeploysWithoutDeadlock()
    {
        const int tenantCount = 6;
        var loadTenants = Enumerable.Range(0, tenantCount)
            .Select(i => $"tenant_load_{i:D2}").ToArray();

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetDemoState();
            DropLoadTenants(loadTenants);

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetDemoProductPath("PostgreSQL", ProductName);
            config["ScriptTokens:TenantCRMDb"] = _mainDb;
            ClearTargetFilters(config);

            try
            {
                // Quench #1: deploy shared infra (onboard_tenant proc + public tables).
                RunSchemaQuench();

                // Onboard many tenants, then a single quench fans TenantWorkspace out across all
                // of them in parallel — the contention shape that triggers the catalog deadlock.
                for (var i = 0; i < tenantCount; i++)
                    OnboardTenant(loadTenants[i], $"Load Tenant {i}", (i % 3) + 1);

                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // Every tenant schema must be fully deployed — no iteration lost to an
                // unrecovered deadlock.
                foreach (var tenant in loadTenants)
                {
                    AssertTableExists(tenant, "customers");
                    AssertTableExists(tenant, "activities");
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");
                }
            }
            finally
            {
                DropLoadTenants(loadTenants);
            }
        }
    }

    private void DropLoadTenants(IEnumerable<string> schemas)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        foreach (var s in schemas)
        {
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{s}\" CASCADE;";
            cmd.ExecuteNonQuery();
        }
        // Clear the tenant rows so a re-run starts clean (table may not exist before quench #1).
        try
        {
            cmd.CommandText = "DELETE FROM public.tenants WHERE name LIKE 'tenant_load_%';";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // public.tenants not yet deployed — nothing to clean.
        }
        conn.Close();
    }

    [Test]
    public void Full_Lifecycle_Fresh_Deploy_Onboard_Three_Then_Selective_Onboard_Fourth()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetDemoState();

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetDemoProductPath("PostgreSQL", ProductName);
            // Re-point the demo's TenantCRMDb token at the CI-provisioned TestMain database
            // so the demo deploys without creating its own database.
            config["ScriptTokens:TenantCRMDb"] = _mainDb;
            ClearTargetFilters(config);

            try
            {
                // ----- Quench #1: empty public.tenants → Shared deploys; TenantWorkspace iterates 0 schemas.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                AssertTableExists("public", "tenants");
                AssertTableExists("public", "plans");
                AssertTableExists("public", "countries");
                AssertTableExists("public", "global_audit_log");
                AssertProcedureExists("public", "onboard_tenant");

                // Plans + Countries seed via DataDelivery.
                Assert.That(ScalarCount("SELECT COUNT(*) FROM public.plans"), Is.EqualTo(3),
                    "Shared template's Plans seed must deliver 3 rows.");
                Assert.That(ScalarCount("SELECT COUNT(*) FROM public.countries"), Is.EqualTo(8),
                    "Shared template's Countries seed must deliver 8 rows.");

                foreach (var tenant in InitialTenants)
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // ----- Onboard three tenants via public.onboard_tenant.
                foreach (var (name, displayName, planId) in new[]
                         {
                             ("tenant_acme", "Acme Corporation", 2),
                             ("tenant_beta", "Beta Industries", 1),
                             ("tenant_globex", "Globex Holdings", 3)
                         })
                {
                    OnboardTenant(name, displayName, planId);
                }

                Assert.That(ScalarCount("SELECT COUNT(*) FROM public.tenants WHERE status = 'Active'"),
                    Is.EqualTo(3), "onboard_tenant should have inserted 3 active tenant rows.");
                foreach (var tenant in InitialTenants)
                {
                    Assert.That(ScalarCount(
                        $"SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '{tenant}'"),
                        Is.EqualTo(1), $"onboard_tenant should have created schema \"{tenant}\".");
                }

                // ----- Quench #2: TenantWorkspace iterates over the three tenants.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in InitialTenants)
                {
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");
                }

                // ----- Assert per-tenant structure.
                foreach (var tenant in InitialTenants)
                {
                    AssertTableExists(tenant, "customers");
                    AssertTableExists(tenant, "contacts");
                    AssertTableExists(tenant, "activities");
                    AssertTableExists(tenant, "activity_types");
                    AssertProcedureExists(tenant, "add_customer");
                    AssertProcedureExists(tenant, "record_activity");
                    AssertFunctionExists(tenant, "get_customer_lifetime_value");
                    AssertViewExists(tenant, "active_customers");
                    AssertTriggerExists(tenant, "customers", "trg_customers_audit_last_modified");
                    AssertMaterializedViewExists(tenant, "mv_active_customer_count");
                }
                AssertIdenticalColumnsAcrossSchemas(InitialTenants, "customers");
                AssertIdenticalColumnsAcrossSchemas(InitialTenants, "activities");

                // ----- Cross-schema FK: tenant.customers.country_code → public.countries.code.
                foreach (var tenant in InitialTenants)
                {
                    var crossSchemaFk = ScalarCount($@"
SELECT COUNT(*) FROM information_schema.referential_constraints rc
  INNER JOIN information_schema.key_column_usage kcu
    ON rc.constraint_name = kcu.constraint_name AND rc.constraint_schema = kcu.constraint_schema
  INNER JOIN information_schema.constraint_column_usage ccu
    ON rc.unique_constraint_name = ccu.constraint_name AND rc.unique_constraint_schema = ccu.constraint_schema
WHERE kcu.table_schema = '{tenant}' AND kcu.table_name = 'customers'
  AND rc.constraint_name = 'fk_customers_countries'
  AND ccu.table_schema = 'public' AND ccu.table_name = 'countries'");
                    Assert.That(crossSchemaFk, Is.GreaterThanOrEqualTo(1),
                        $"Tenant '{tenant}' must have fk_customers_countries pointing at public.countries.");
                }

                // ----- Migration tracking: Migration_001_BackfillCustomerCountryCode per tenant.
                // activity_types is seeded via DataDelivery (MergeType=Insert) — see assertion
                // immediately below — and is not tracked as a migration.
                foreach (var tenant in InitialTenants)
                {
                    AssertMigrationTracked(TenantWorkspaceTemplate, tenant,
                        "Before Scripts/Migration_001_BackfillCustomerCountryCode.sql");

                    // DataDelivery should have inserted the 4 default activity types per tenant.
                    Assert.That(ScalarCount($"SELECT COUNT(*) FROM \"{tenant}\".activity_types"),
                        Is.EqualTo(4), $"Tenant '{tenant}' should have 4 DataDelivery-seeded activity types.");
                }

                // ----- Procedures wire to public.global_audit_log via {{SchemaName}} token resolution.
                ExecuteOnMainDb(@"CALL ""tenant_acme"".add_customer('Wile E. Coyote', 'wile@acme.example', 'US', NULL);");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM public.global_audit_log WHERE tenant_name = 'tenant_acme' AND event_type = 'CustomerAdded'"),
                    Is.GreaterThanOrEqualTo(1),
                    "tenant_acme.add_customer must have written to the global audit log with tenant_name='tenant_acme'.");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM \"tenant_acme\".customers WHERE customer_name = 'Wile E. Coyote'"),
                    Is.EqualTo(1), "Customer should be in tenant_acme.customers.");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM \"tenant_beta\".customers"),
                    Is.EqualTo(0), "tenant_beta must NOT have received the customer — schema isolation.");

                // ----- Quench #3: idempotent re-quench — migrations should skip.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in InitialTenants)
                {
                    var migrationCount = ScalarCount(
                        $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantWorkspaceTemplate}' AND schema_name = '{tenant}'");
                    Assert.That(migrationCount, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have exactly 1 tracked migration after re-quench (no duplicates).");
                }

                // Slice-3 audit B1: per-tenant materialized views must survive a second quench.
                // Pre-fix, ProductOwnership rows were not template+schema scoped on the existing-
                // views read path, so sibling tenants saw each other's mv_active_customer_count as
                // "removed from product" and dropped them under parallel iterations. Post-fix the
                // ownership read is scoped to (template_name, schema). User-visible demo form of
                // the regression covered by SchemaTemplateHappyPathTests.
                foreach (var tenant in InitialTenants)
                    AssertMaterializedViewExists(tenant, "mv_active_customer_count");

                // ----- Onboard a fourth tenant; selective quench targets only that tenant.
                OnboardTenant(FourthTenant, "Fourth Co", 1);

                var preActivityTypeCounts = InitialTenants
                    .ToDictionary(t => t, t => ScalarCount($"SELECT COUNT(*) FROM \"{t}\".activity_types"));
                var preMigrationCounts = InitialTenants
                    .ToDictionary(t => t, t => ScalarCount(
                        $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantWorkspaceTemplate}' AND schema_name = '{t}'"));

                config["Target:Schemas:0"] = FourthTenant;
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                AssertTableExists(FourthTenant, "customers");
                AssertTableExists(FourthTenant, "activity_types");
                AssertProcedureExists(FourthTenant, "add_customer");
                AssertMaterializedViewExists(FourthTenant, "mv_active_customer_count");
                AssertMigrationTracked(TenantWorkspaceTemplate, FourthTenant,
                    "Before Scripts/Migration_001_BackfillCustomerCountryCode.sql");
                Assert.That(ScalarCount($"SELECT COUNT(*) FROM \"{FourthTenant}\".activity_types"),
                    Is.EqualTo(4), "Fourth tenant should have 4 DataDelivery-seeded activity types.");

                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: {FourthTenant}] Successfully Quenched");

                foreach (var tenant in InitialTenants)
                {
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                    Assert.That(ScalarCount($"SELECT COUNT(*) FROM \"{tenant}\".activity_types"),
                        Is.EqualTo(preActivityTypeCounts[tenant]),
                        $"Tenant '{tenant}' activity-type row count must be unchanged by selective quench.");

                    Assert.That(ScalarCount(
                            $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantWorkspaceTemplate}' AND schema_name = '{tenant}'"),
                        Is.EqualTo(preMigrationCounts[tenant]),
                        $"Tenant '{tenant}' migration tracking must be unchanged by selective quench.");
                }

                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM \"tenant_acme\".customers WHERE customer_name = 'Wile E. Coyote'"),
                    Is.EqualTo(1), "tenant_acme's customer data must survive a selective quench targeting another tenant.");
            }
            finally
            {
                ClearTargetFilters(FactoryContainer.Resolve<IConfigurationRoot>());
                FactoryContainer.Resolve<IConfigurationRoot>()["ScriptTokens:TenantCRMDb"] = null;
                ResetDemoState();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private static void ClearTargetFilters(IConfigurationRoot config)
    {
        foreach (var dim in new[] { "Templates", "Databases", "Schemas" })
            foreach (var child in config.GetSection($"Target:{dim}").GetChildren().ToList())
                config[$"Target:{dim}:{child.Key}"] = null;
    }

    private void ResetDemoState()
    {
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        // Drop tenant schemas with CASCADE — catches tables, FKs, procs, functions, triggers.
        foreach (var tenant in InitialTenants.Concat(new[] { FourthTenant }))
        {
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant}\" CASCADE;";
            cmd.ExecuteNonQuery();
        }

        // Drop the demo's public-schema objects.
        cmd.CommandText = @"
DROP PROCEDURE IF EXISTS public.onboard_tenant(VARCHAR, VARCHAR, INTEGER);
DROP TABLE IF EXISTS public.tenants CASCADE;
DROP TABLE IF EXISTS public.plans CASCADE;
DROP TABLE IF EXISTS public.countries CASCADE;
DROP TABLE IF EXISTS public.global_audit_log CASCADE;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @$"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{ProductName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        -- Clear the demo product's ownership rows so a fresh run starts clean.
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" = '{ProductName}';
    END IF;
END;
$$;";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void OnboardTenant(string name, string displayName, int planId)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CALL public.onboard_tenant('{name}', '{displayName}', {planId});";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void ExecuteOnMainDb(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void AssertTableExists(string schema, string tableName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{tableName}' AND table_type = 'BASE TABLE'");
        Assert.That(count, Is.EqualTo(1), $"Expected table \"{schema}\".{tableName} to exist after quench.");
    }

    private void AssertProcedureExists(string schema, string procName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM information_schema.routines WHERE specific_schema = '{schema}' AND routine_name = '{procName}' AND routine_type = 'PROCEDURE'");
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            $"Expected procedure \"{schema}\".{procName} to exist after quench.");
    }

    private void AssertFunctionExists(string schema, string fnName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM information_schema.routines WHERE specific_schema = '{schema}' AND routine_name = '{fnName}' AND routine_type = 'FUNCTION'");
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            $"Expected function \"{schema}\".{fnName} to exist after quench.");
    }

    private void AssertViewExists(string schema, string viewName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM information_schema.views WHERE table_schema = '{schema}' AND table_name = '{viewName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected view \"{schema}\".{viewName} to exist after quench.");
    }

    private void AssertTriggerExists(string schema, string tableName, string triggerName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(DISTINCT trigger_name) FROM information_schema.triggers WHERE event_object_schema = '{schema}' AND event_object_table = '{tableName}' AND trigger_name = '{triggerName}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected trigger \"{schema}\".{triggerName} on table {tableName} to exist after quench.");
    }

    private void AssertMaterializedViewExists(string schema, string mvName)
    {
        // pg_matviews lists installed materialized views per schema — the iteration must have
        // created the per-tenant MV with the right name in the iterating schema.
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM pg_matviews WHERE schemaname = '{schema}' AND matviewname = '{mvName}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected materialized view \"{schema}\".{mvName} to exist after quench.");
    }

    private void AssertIdenticalColumnsAcrossSchemas(IReadOnlyList<string> schemas, string tableName)
    {
        List<string> reference = null;
        foreach (var schema in schemas)
        {
            var cols = QueryRows(
                $"SELECT column_name || ':' || data_type FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{tableName}' ORDER BY ordinal_position");
            reference ??= cols;
            Assert.That(cols, Is.EquivalentTo(reference),
                $"Schema '{schema}' table '{tableName}' columns differ from reference set — per-iteration structure is not identical.");
        }
    }

    private void AssertMigrationTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND \"ScriptPath\" = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected a tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}').");
    }

    private int ScalarCount(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result);
    }

    private List<string> QueryRows(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader[0]?.ToString());
        conn.Close();
        return rows;
    }
}
