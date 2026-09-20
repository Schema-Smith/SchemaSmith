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
/// Slice-3 schema-template integration tests (design §10.4). Verifies the engine
/// core end-to-end against live PostgreSQL: multi-tenant happy path, TemplateOrder
/// enforcement, reserved-name discovery rejection, per-iteration token resolution,
/// migration tracking per (template, schema), and cross-schema FK references.
///
/// <para>Fixtures pre-create the tenant schemas — slice 4 wires up
/// <c>CreateSchemaIfMissing</c>. Failure-isolation modes (<c>ContinueOnSchemaFailure</c> /
/// <c>ContinueOnDatabaseFailure</c>), tenant lifecycle (onboarding / offboarding), and
/// selective execution scope (<c>Target.*</c>) are deferred to later slices and out of
/// scope here.</para>
/// </summary>
[Category("PostgreSQL")]
public class SchemaTemplateHappyPathTests
{
    private const string ProductName = "SchemaTemplateProduct";
    private const string TenantBodyTemplate = "TenantBody";
    private const string SharedTemplate = "Shared";

    private static readonly string[] DefaultTenants = ["tenant_acme", "tenant_beta", "tenant_globex"];

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public SchemaTemplateHappyPathTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [Test]
    public void Happy_Path_Multi_Tenant_Deploy_Creates_Identical_Per_Tenant_Structure_And_Shared_Content_Once()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                _progressLog.Received(1).Info($"Completed quench of {ProductName}");
                _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] Successfully Quenched");
                foreach (var tenant in DefaultTenants)
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // Shared content in public: schematemplate_tenants, lookup, shared_audit + seed row.
                AssertTableExists("public", "schematemplate_tenants");
                AssertTableExists("public", "lookup");
                AssertTableExists("public", "shared_audit");
                Assert.That(ScalarCount("SELECT COUNT(*) FROM public.lookup WHERE lookup_id = 1 AND code = 'ALPHA'"),
                    Is.EqualTo(1), "Shared SeedLookup migration should have inserted exactly one row.");

                // Regression for slice-3 audit B1: Shared template's ProductOwnership rows
                // must carry template_name = 'Shared' AND survive TenantBody iteration's
                // FixupTableOwnership prune + ModifiedTableQuench drop pass.
                foreach (var sharedTable in new[] { "schematemplate_tenants", "lookup", "shared_audit" })
                {
                    var ownershipCount = ScalarCount(
                        $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{ProductName}' AND \"Schema\" = 'public' AND \"TableName\" = '{sharedTable}' AND \"IndexName\" IS NULL AND template_name = '{SharedTemplate}'");
                    Assert.That(ownershipCount, Is.EqualTo(1),
                        $"Shared table public.{sharedTable} must have exactly one ProductOwnership row scoped to template '{SharedTemplate}' (survived tenant iterations).");
                }

                // Per-tenant structure.
                foreach (var tenant in DefaultTenants)
                {
                    AssertTableExists(tenant, "customers");
                    AssertTableExists(tenant, "orders");
                }
                AssertIdenticalColumnsAcrossSchemas(DefaultTenants, "customers");
                AssertIdenticalColumnsAcrossSchemas(DefaultTenants, "orders");

                // Migration tracking.
                AssertMigrationTracked(SharedTemplate, "", "Before Scripts/SeedLookup.sql");
                foreach (var tenant in DefaultTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant, "Before Scripts/SeedTenantMarker.sql");

                // Per-iteration marker INSERT landed.
                foreach (var tenant in DefaultTenants)
                {
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM \"{tenant}\".customers WHERE marker = '{tenant}_marker'");
                    Assert.That(count, Is.EqualTo(1), $"Tenant '{tenant}' should have its per-iteration marker row.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Template_Order_Is_Enforced_Shared_Completes_Before_Any_TenantBody_Iteration_Starts()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                var infos = _progressLog.ReceivedCalls()
                    .Where(c => c.GetMethodInfo().Name == nameof(ILog.Info))
                    .Select(c => (string)c.GetArguments()[0])
                    .ToList();

                var sharedIdx = infos.FindIndex(s => s == $"Quenching Template: {SharedTemplate}");
                var tenantBodyIdx = infos.FindIndex(s => s == $"Quenching Template: {TenantBodyTemplate}");
                Assert.That(sharedIdx, Is.GreaterThanOrEqualTo(0), "Shared template start log not found");
                Assert.That(tenantBodyIdx, Is.GreaterThan(sharedIdx),
                    "TenantBody must start AFTER Shared starts (TemplateOrder).");

                var sharedDoneIdx = infos.FindIndex(s => s == $"[{_server}].[{_mainDb}] Successfully Quenched");
                Assert.That(sharedDoneIdx, Is.GreaterThan(sharedIdx),
                    "Shared template must reach 'Successfully Quenched' (no [Schema:] prefix) after starting.");
                foreach (var tenant in DefaultTenants)
                {
                    var iterIdx = infos.FindIndex(s => s == $"[{_server}].[{_mainDb}] [Schema: {tenant}] Begin Quench");
                    Assert.That(iterIdx, Is.GreaterThan(sharedDoneIdx),
                        $"Tenant '{tenant}' iteration must start AFTER Shared completes.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Reserved_Schema_Name_From_Discovery_Aborts_Template_With_Clear_Error()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);

            // Insert a poisoned 'public' tenant row so discovery returns a reserved name.
            ExecuteOnMainDb("INSERT INTO public.schematemplate_tenants (name) VALUES ('public')");

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.Received().Error(Arg.Is<string>(s =>
                    s.Contains("Schema discovery FAILED") &&
                    s.Contains("reserved schema name 'public'")));
                _environment.Received().Exit(2);

                // Other discovered schemas in the same run must NOT be deployed.
                foreach (var tenant in DefaultTenants)
                {
                    var customers = ScalarCount(
                        $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{tenant}' AND table_name = 'customers'");
                    Assert.That(customers, Is.EqualTo(0),
                        $"Tenant '{tenant}' must not have customers — TenantBody must not have dispatched after reserved-name discovery failure.");
                }
            }
            finally
            {
                ExecuteOnMainDb("DELETE FROM public.schematemplate_tenants WHERE name = 'public'");
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Per_Iteration_Query_Token_Resolves_Differently_Per_Tenant_And_Lands_In_Function_Body()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Each tenant's get_tenant_label function body should contain the tenant-specific
                // resolved {{TenantLabel}} value: '<tenant>_label'. pg_get_functiondef returns the
                // full body.
                foreach (var tenant in DefaultTenants)
                {
                    var fnDef = ScalarString($@"
SELECT pg_get_functiondef(p.oid)
  FROM pg_proc p
  INNER JOIN pg_namespace n ON p.pronamespace = n.oid
  WHERE n.nspname = '{tenant}' AND p.proname = 'get_tenant_label'");
                    Assert.That(fnDef, Is.Not.Null.And.Not.Empty,
                        $"Tenant '{tenant}' should have get_tenant_label function deployed.");
                    Assert.That(fnDef, Contains.Substring($"{tenant}_label"),
                        $"Tenant '{tenant}' function body should carry its iteration-resolved TenantLabel.");

                    foreach (var otherTenant in DefaultTenants.Where(t => t != tenant))
                        Assert.That(fnDef, Does.Not.Contain($"{otherTenant}_label"),
                            $"Tenant '{tenant}' function body must not contain '{otherTenant}_label'.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Second_Quench_Skips_Per_Tenant_Migration_Scripts_Tracking_Is_Per_Template_And_Schema()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant, "Before Scripts/SeedTenantMarker.sql");
                AssertMigrationTracked(SharedTemplate, "", "Before Scripts/SeedLookup.sql");

                foreach (var tenant in DefaultTenants)
                    ExecuteOnMainDb($"UPDATE \"{tenant}\".customers SET marker = 'mutated' WHERE customer_id = 1");

                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var marker = ScalarString(
                        $"SELECT marker FROM \"{tenant}\".customers WHERE customer_id = 1");
                    Assert.That(marker, Is.EqualTo("mutated"),
                        $"Tenant '{tenant}': second quench must skip the migration — marker should still be 'mutated'.");
                }

                foreach (var tenant in DefaultTenants)
                {
                    var trackingCount = ScalarCount(
                        $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}' AND \"ScriptPath\" = 'Before Scripts/SeedTenantMarker.sql'");
                    Assert.That(trackingCount, Is.EqualTo(1),
                        $"Tenant '{tenant}' should still have exactly one tracking row for SeedTenantMarker.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Schema_Template_DataDelivery_MergesPerIteration()
    {
        // Slice-3 audit B3 regression: a schema-template table with DataDelivery.MergeType
        // configured must deliver its content into EACH tenant schema. Pre-fix, the literal
        // "{{SchemaName}}" reached MergeScriptHelper, the catalog probes returned empty
        // result sets, and the emitted MERGE referenced "{{SchemaName}}"."lookups" —
        // silently broken end-to-end.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Each tenant's lookups table must contain the seeded rows.
                foreach (var tenant in DefaultTenants)
                {
                    AssertTableExists(tenant, "lookups");
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM \"{tenant}\".lookups");
                    Assert.That(count, Is.EqualTo(3),
                        $"Tenant '{tenant}' must have all three lookups rows delivered.");

                    var codes = QueryRows(
                        $"SELECT code FROM \"{tenant}\".lookups ORDER BY lookup_id");
                    Assert.That(codes, Is.EqualTo(new[] { "ALPHA", "BETA", "GAMMA" }),
                        $"Tenant '{tenant}' lookups codes must match the content file.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Cross_Schema_Foreign_Key_To_Shared_Lookup_Is_Created_Per_Tenant()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var sameIterationFk = ScalarCount($@"
SELECT COUNT(*) FROM information_schema.table_constraints tc
  INNER JOIN information_schema.referential_constraints rc ON tc.constraint_name = rc.constraint_name AND tc.constraint_schema = rc.constraint_schema
  INNER JOIN information_schema.constraint_column_usage ccu ON rc.unique_constraint_name = ccu.constraint_name AND rc.unique_constraint_schema = ccu.constraint_schema
  WHERE tc.constraint_type = 'FOREIGN KEY'
    AND tc.table_schema = '{tenant}'
    AND tc.table_name = 'orders'
    AND tc.constraint_name = 'fk_orders_customers'
    AND ccu.table_schema = '{tenant}'
    AND ccu.table_name = 'customers'");
                    Assert.That(sameIterationFk, Is.GreaterThanOrEqualTo(1),
                        $"Tenant '{tenant}' must have fk_orders_customers same-iteration FK.");

                    var crossSchemaFk = ScalarCount($@"
SELECT COUNT(*) FROM information_schema.table_constraints tc
  INNER JOIN information_schema.referential_constraints rc ON tc.constraint_name = rc.constraint_name AND tc.constraint_schema = rc.constraint_schema
  INNER JOIN information_schema.constraint_column_usage ccu ON rc.unique_constraint_name = ccu.constraint_name AND rc.unique_constraint_schema = ccu.constraint_schema
  WHERE tc.constraint_type = 'FOREIGN KEY'
    AND tc.table_schema = '{tenant}'
    AND tc.table_name = 'orders'
    AND tc.constraint_name = 'fk_orders_lookup'
    AND ccu.table_schema = 'public'
    AND ccu.table_name = 'lookup'");
                    Assert.That(crossSchemaFk, Is.GreaterThanOrEqualTo(1),
                        $"Tenant '{tenant}' must have fk_orders_lookup cross-schema FK pointing at public.lookup.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // [ALWAYS] / WhatIf / Tenant offboarding+re-onboarding / MaterializedView scenarios were
    // previously [Ignore]'d on this PG mirror to keep connection-pool pressure below the default
    // max_connections=100. The test container raises it (matching the CI override and the Demos PG
    // compose), so all four scenarios run alongside the existing 7 PG tests in this fixture.

    [Test]
    public void Always_Tagged_Script_Runs_Every_Quench_Per_Iteration_And_Is_Not_Tracked()
    {
        // Design §6.7: a [ALWAYS] script runs every quench and is not added to
        // CompletedMigrationScripts. In a schema template the script runs once per iteration
        // per quench — two quenches against N tenants produce 2*N audit rows total.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var rowsAfterFirst = ScalarCount(
                        $"SELECT COUNT(*) FROM public.shared_audit WHERE tenant = '{tenant}' AND note = 'always-touched'");
                    Assert.That(rowsAfterFirst, Is.EqualTo(1),
                        $"After quench #1 tenant '{tenant}' should have exactly one [ALWAYS] audit row.");
                }

                foreach (var tenant in DefaultTenants)
                {
                    var tracked = ScalarCount(
                        $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}' AND \"ScriptPath\" LIKE '%TouchAudit%'");
                    Assert.That(tracked, Is.EqualTo(0),
                        $"Tenant '{tenant}': [ALWAYS] script must not appear in CompletedMigrationScripts.");
                }

                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var rowsAfterSecond = ScalarCount(
                        $"SELECT COUNT(*) FROM public.shared_audit WHERE tenant = '{tenant}' AND note = 'always-touched'");
                    Assert.That(rowsAfterSecond, Is.EqualTo(2),
                        $"After quench #2 tenant '{tenant}' should have two [ALWAYS] audit rows ([ALWAYS] re-ran).");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void WhatIf_With_Schema_Template_Iterates_Per_Tenant_And_Makes_No_State_Changes()
    {
        // Design §5.10: WhatIf in schema templates uses the same iteration model — every
        // "execute" becomes "log the SQL that would have run." Tables must already exist
        // (PostgreSQL's MissingTableAndColumnQuench / ModifiedTableQuench probe the catalog
        // even in WhatIf mode), so the test first does a real quench, then re-runs with
        // WhatIfONLY=true and asserts the second pass is read-only.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                // First quench: real deploy so tables exist for the WhatIf catalog probe.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Capture per-tenant state BEFORE WhatIf (single connection — PG pool is tight at
                // this fan-out so combine into fewer round-trips).
                var stateBefore = CaptureWhatIfStateSnapshot();

                _progressLog.ClearReceivedCalls();

                // Second quench: WhatIf mode. Must NOT modify anything.
                config["WhatIfONLY"] = "true";
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                foreach (var tenant in DefaultTenants)
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                foreach (var tenant in DefaultTenants)
                {
                    _progressLog.Received().Info(Arg.Is<string>(s =>
                        s.Contains($"[Schema: {tenant}]") && s.Contains("[WhatIf] Before database scripts:")));
                }

                // State must be unchanged after WhatIf.
                var stateAfter = CaptureWhatIfStateSnapshot();
                Assert.That(stateAfter, Is.EqualTo(stateBefore),
                    "WhatIf must not modify per-tenant marker, tracking row count, or [ALWAYS] audit row count.");
            }
            finally
            {
                config["WhatIfONLY"] = "false";
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void WhatIf_With_Target_Schemas_Filter_Only_Logs_For_Targeted_Tenant_And_Makes_No_State_Changes()
    {
        // Post-slice-8 cleanup (Commit C): WhatIf + Target.Schemas composition. A targeted WhatIf
        // run must produce iteration log output ONLY for the filtered tenant (no [Schema:
        // tenant_beta] / [Schema: tenant_globex] WhatIf chatter), and must leave every tenant's
        // state untouched. Pins the read-only-AND-filtered composition on PG.
        const string targetTenant = "tenant_acme";
        var siblingTenants = new[] { "tenant_beta", "tenant_globex" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                // First quench: real deploy so all three tenants exist + tracking rows land.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Snapshot per-tenant state BEFORE the targeted WhatIf.
                var stateBefore = CaptureWhatIfStateSnapshot();

                _progressLog.ClearReceivedCalls();

                // Second quench: WhatIfONLY + Target.Schemas scoped to a single tenant.
                config["WhatIfONLY"] = "true";
                config["Target:Schemas:0"] = targetTenant;
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // Targeted tenant runs (under WhatIf): Successfully Quenched line appears + a
                // WhatIf Before-scripts line carries its [Schema:] prefix.
                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: {targetTenant}] Successfully Quenched");
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"[Schema: {targetTenant}]") && s.Contains("[WhatIf] Before database scripts:")));

                // Sibling tenants must produce NO iteration log lines — the filter rules them out.
                foreach (var sibling in siblingTenants)
                {
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {sibling}] Successfully Quenched");
                    _progressLog.DidNotReceive().Info(Arg.Is<string>(s =>
                        s.Contains($"[Schema: {sibling}]") && s.Contains("[WhatIf]")));
                    _progressLog.DidNotReceive().Info(Arg.Is<string>(s =>
                        s.Contains($"[Schema: {sibling}]") && s.Contains("Begin Quench")));
                }

                // No state change across any tenant.
                var stateAfter = CaptureWhatIfStateSnapshot();
                Assert.That(stateAfter, Is.EqualTo(stateBefore),
                    "WhatIf+Target.Schemas must not modify per-tenant marker, tracking row count, or [ALWAYS] audit row count for any tenant.");
            }
            finally
            {
                config["WhatIfONLY"] = "false";
                foreach (var child in config.GetSection("Target:Schemas").GetChildren().ToList())
                    config[$"Target:Schemas:{child.Key}"] = null;
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void DropTablesRemovedFromProduct_With_Schema_Template_Only_Affects_Iterating_Tenant_Not_Siblings()
    {
        // Post-slice-8 cleanup (Commit C): the customer-facing form of the slice-3 audit B1 fix.
        // Deploy 3 tenants, then re-quench with DropTablesRemovedFromProduct=true. Each tenant's
        // iteration evaluates its OWN schema's owned-by-product tables against the package; the
        // package still defines customers for every tenant, so every tenant's customers must
        // SURVIVE the drop pass (a regression here would have caused sibling iterations to see
        // a different tenant's customers as "removed" and drop them).
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                // Quench #1: full multi-tenant deploy.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                foreach (var tenant in DefaultTenants)
                    AssertTableExists(tenant, "customers");

                // Quench #2: enable DropTablesRemovedFromProduct.
                config["DropTablesRemovedFromProduct"] = "true";
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    AssertTableExists(tenant, "customers");
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM \"{tenant}\".customers WHERE marker = '{tenant}_marker'");
                    Assert.That(count, Is.EqualTo(1),
                        $"Tenant '{tenant}': per-iteration customers row must survive the cross-tenant drop pass.");
                }
            }
            finally
            {
                config["DropTablesRemovedFromProduct"] = null;
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Tenant_Offboarding_And_Re_Onboarding_Skips_Migrations_On_Return()
    {
        // Design §6.8 scenario D — see SQL Server mirror for the full scenario.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                AssertMigrationTracked(TenantBodyTemplate, "tenant_beta", "Before Scripts/SeedTenantMarker.sql");

                ExecuteOnMainDb("UPDATE \"tenant_beta\".customers SET marker = 'mutated' WHERE customer_id = 1");

                ExecuteOnMainDb("DELETE FROM public.schematemplate_tenants WHERE name = 'tenant_beta'");

                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_beta] Successfully Quenched");

                var markerAfterOffboard = ScalarString(
                    "SELECT marker FROM \"tenant_beta\".customers WHERE customer_id = 1");
                Assert.That(markerAfterOffboard, Is.EqualTo("mutated"),
                    "Offboarded tenant must not be re-touched by any subsequent quench.");

                AssertMigrationTracked(TenantBodyTemplate, "tenant_beta", "Before Scripts/SeedTenantMarker.sql");

                ExecuteOnMainDb("INSERT INTO public.schematemplate_tenants (name) VALUES ('tenant_beta')");

                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_beta] Successfully Quenched");

                var markerAfterReOnboard = ScalarString(
                    "SELECT marker FROM \"tenant_beta\".customers WHERE customer_id = 1");
                Assert.That(markerAfterReOnboard, Is.EqualTo("mutated"),
                    "Re-onboarded tenant must skip already-tracked run-once migrations.");

                var betaTrackingCount = ScalarCount(
                    $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = 'tenant_beta' AND \"ScriptPath\" = 'Before Scripts/SeedTenantMarker.sql'");
                Assert.That(betaTrackingCount, Is.EqualTo(1),
                    "Re-onboarded tenant must not produce a duplicate tracking row.");
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Materialized_View_Per_Tenant_Is_Created_And_Refreshable()
    {
        // PG schema templates support materialized views — each iteration creates the MV in
        // its own schema, and the MaterializedViewQuench proc is correctly scoped per
        // (template, schema) so sibling iterations don't drop each other's MVs.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var mvCount = ScalarCount(
                        $"SELECT COUNT(*) FROM pg_matviews WHERE schemaname = '{tenant}' AND matviewname = 'mv_customer_count'");
                    Assert.That(mvCount, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have its mv_customer_count materialized view.");
                }

                // Second quench must succeed too — MV ownership scoping holds under parallel
                // iterations (regression guard against the SqlServer IndexedViewQuench bug).
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var mvCount = ScalarCount(
                        $"SELECT COUNT(*) FROM pg_matviews WHERE schemaname = '{tenant}' AND matviewname = 'mv_customer_count'");
                    Assert.That(mvCount, Is.EqualTo(1),
                        $"After second quench, tenant '{tenant}' must still have its mv_customer_count.");
                }
            }
            finally
            {
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Per_Db_Query_Token_Resolves_Once_Across_All_Iterations_While_Iteration_Token_Resolves_Per_Iteration()
    {
        // Post-slice-8 cleanup (Commit B): per-DB query tokens (no {{SchemaName}} reference,
        // direct or transitive) are cached per (server, database) across schema-template
        // iterations. A 3-tenant fan-out must execute the per-DB token's body exactly once
        // (the first iteration; siblings 2 + 3 hit the cache) while the iteration-scoped
        // token's body executes once per iteration (3 times total). The test product has
        // two query tokens with side-effect bodies that INSERT into a counter table, so
        // counter-row counts prove the cache is doing its job.
        const string cacheProduct = "SchemaTemplatePerDbCacheProduct";
        var tenants = new[] { "perdbcache_a", "perdbcache_b", "perdbcache_c" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(cacheProduct);

            using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 0;

                // Drop tenant schemas (prior runs may have left them with the get_tokens function).
                foreach (var tenant in tenants)
                {
                    cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant}\" CASCADE;";
                    cmd.ExecuteNonQuery();
                }

                cmd.CommandText = @"
DROP TABLE IF EXISTS public.token_call_counter CASCADE;
CREATE TABLE public.token_call_counter (
    id SERIAL PRIMARY KEY,
    token_kind VARCHAR(32) NOT NULL,
    iteration_schema VARCHAR(128) NOT NULL);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @$"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{cacheProduct}';
    END IF;
END;
$$;";
                cmd.ExecuteNonQuery();

                foreach (var tenant in tenants)
                {
                    cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{tenant}\";";
                    cmd.ExecuteNonQuery();
                }
                conn.Close();
            }

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", cacheProduct);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // The per-DB token must execute exactly once across all 3 iterations — the first
                // iteration's resolution populates the cache; iterations 2 + 3 read from it and
                // skip the connection round-trip (no counter row inserted).
                var perDbCalls = ScalarCount(
                    "SELECT COUNT(*) FROM public.token_call_counter WHERE token_kind = 'PerDb'");
                Assert.That(perDbCalls, Is.EqualTo(1),
                    $"Per-DB query token must execute exactly once across {tenants.Length} iterations (cached). " +
                    "If this is N, the cache is not engaged and the token is re-running per iteration.");

                // The iteration-scoped token must execute once per iteration (3 times total).
                var iterCalls = ScalarCount(
                    "SELECT COUNT(*) FROM public.token_call_counter WHERE token_kind = 'Iteration'");
                Assert.That(iterCalls, Is.EqualTo(tenants.Length),
                    $"Iteration-scoped query token must execute once per iteration ({tenants.Length} total).");

                // Per-iteration token must carry each tenant's schema in its body — proves caching
                // didn't accidentally swap iteration-scoped values across iterations.
                foreach (var tenant in tenants)
                {
                    var tenantIterRows = ScalarCount(
                        $"SELECT COUNT(*) FROM public.token_call_counter WHERE token_kind = 'Iteration' AND iteration_schema = '{tenant}'");
                    Assert.That(tenantIterRows, Is.EqualTo(1),
                        $"Iteration token for tenant '{tenant}' must have produced exactly one counter row.");
                }

                // The function body in each tenant schema must carry the per-DB token's
                // resolved literal — proves the cached value was substituted into the script,
                // not just shorted out of the connection round-trip.
                foreach (var tenant in tenants)
                {
                    var fnBody = ScalarString(
                        $"SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid WHERE n.nspname = '{tenant}' AND p.proname = 'get_tokens'");
                    Assert.That(fnBody, Does.Contain("perdb_value"),
                        $"Tenant '{tenant}' function must carry the resolved per-DB token literal — cache must merge cached value back into the iteration's substitution list.");
                    Assert.That(fnBody, Does.Contain($"iter_value_{tenant}"),
                        $"Tenant '{tenant}' function must carry its own iteration-scoped token value (proves no cross-iteration leakage).");
                }
            }
            finally
            {
                using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
                {
                    conn.Open();
                    conn.ChangeDatabase(_mainDb);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 0;
                    foreach (var tenant in tenants)
                    {
                        cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant}\" CASCADE;";
                        cmd.ExecuteNonQuery();
                    }
                    cmd.CommandText = @$"
DROP TABLE IF EXISTS public.token_call_counter CASCADE;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{cacheProduct}';
    END IF;
END;
$$;";
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Single-connection snapshot of the three per-tenant metrics the WhatIf test asserts on
    /// (marker, tracking row count, [ALWAYS] audit row count). Reduces PG connection pressure
    /// — at this fan-out size the cross-fixture connection count is still tight enough that
    /// consolidating round-trips pays for itself.
    /// </summary>
    private string CaptureWhatIfStateSnapshot()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        var rows = new List<string>();
        foreach (var tenant in DefaultTenants)
        {
            cmd.CommandText = $@"
SELECT
    COALESCE((SELECT marker FROM ""{tenant}"".customers WHERE customer_id = 1), '<null>'),
    (SELECT COUNT(*) FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}'),
    (SELECT COUNT(*) FROM public.shared_audit WHERE tenant = '{tenant}' AND note = 'always-touched')";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                rows.Add($"{tenant}|{reader[0]}|{reader[1]}|{reader[2]}");
            reader.Close();
        }
        conn.Close();
        return string.Join(";", rows);
    }

    private void SetupSharedMocks()
    {
        // Re-assert PostgreSQL's Target:* on the shared config before the quench reads it live: a sibling
        // engine fixture (MySQL/MariaDb) running its OneTimeSetUp on the parallel worker lane can overwrite
        // the shared Target keys. Every test body holds SharedLockObject and the fixtures now write Target
        // under that same lock, so once re-asserted here no fixture write can stomp it before RunSchemaQuench.
        FixtureSetup.ApplyTargetConfig(FactoryContainer.Resolve<IConfigurationRoot>());

        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private void RunSchemaQuench()
    {
        Program.Main(["SkipKindlingForge"]);
    }

    private static void ClearCheckpointsForProduct()
    {
        // Delegate to the live FileCheckpointManager so its in-memory cache is also cleared
        // (the static singleton survives across tests in the same NUnit run; deleting files on
        // disk alone leaves stale cache entries that skip checkpoint-tracked steps).
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);
    }

    private void ResetTrackingAndCreateTenantSchemas(IEnumerable<string> tenants)
    {
        ClearCheckpointsForProduct();
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = @$"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{ProductName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" = '{ProductName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'schematemplate_tenants') THEN
        DELETE FROM public.schematemplate_tenants;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'lookup') THEN
        DELETE FROM public.lookup;
    END IF;
END;
$$;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, tenants);

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{tenant}\";";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS public.schematemplate_tenants (name VARCHAR(128) NOT NULL CONSTRAINT pk_schematemplate_tenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO public.schematemplate_tenants (name) VALUES ('{tenant}');";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    private void DropTenantSchemas(IEnumerable<string> tenants)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        DropTenantSchemasInternal(cmd, tenants);

        cmd.CommandText = @$"
DROP TABLE IF EXISTS public.shared_audit CASCADE;
DROP TABLE IF EXISTS public.lookup CASCADE;
DROP TABLE IF EXISTS public.schematemplate_tenants CASCADE;
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

    private static void DropTenantSchemasInternal(IDbCommand cmd, IEnumerable<string> tenants)
    {
        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant}\" CASCADE;";
            cmd.ExecuteNonQuery();
        }
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
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{tableName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected table \"{schema}\".{tableName} to exist after quench.");
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

    private string ScalarString(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
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
