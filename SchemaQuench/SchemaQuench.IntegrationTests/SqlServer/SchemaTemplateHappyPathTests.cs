// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Slice-3 schema-template integration tests (design §10.4). Verifies the engine
/// core end-to-end against live SQL Server: multi-tenant happy path, TemplateOrder
/// enforcement, reserved-name discovery rejection, per-iteration token resolution,
/// migration tracking per (template, schema), and cross-schema FK references.
///
/// <para>Fixtures pre-create the tenant schemas — slice 4 wires up
/// <c>CreateSchemaIfMissing</c>. Failure-isolation modes (<c>ContinueOnSchemaFailure</c> /
/// <c>ContinueOnDatabaseFailure</c>), tenant lifecycle (onboarding / offboarding), and
/// selective execution scope (<c>Target.*</c>) are deferred to later slices and out of
/// scope here.</para>
/// </summary>
[Category("SqlServer")]
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
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                // No errors; non-zero-exit not invoked.
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                _progressLog.Received(1).Info($"Completed quench of {ProductName}");
                _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] Successfully Quenched");
                foreach (var tenant in DefaultTenants)
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // Shared content present once in dbo: SchemaTemplateTenants, Lookup, SharedAudit + seed row.
                AssertTableExists("dbo", "SchemaTemplateTenants");
                AssertTableExists("dbo", "Lookup");
                AssertTableExists("dbo", "SharedAudit");
                Assert.That(ScalarCount("SELECT COUNT(*) FROM dbo.Lookup WHERE LookupID = 1 AND Code = N'ALPHA'"),
                    Is.EqualTo(1), "Shared SeedLookup migration should have inserted exactly one row.");

                // Per-tenant structure: Customers + Orders exist in every tenant schema.
                foreach (var tenant in DefaultTenants)
                {
                    AssertTableExists(tenant, "Customers");
                    AssertTableExists(tenant, "Orders");
                }

                // Identical per-tenant structure: column shape matches across tenants.
                AssertIdenticalColumnsAcrossSchemas(DefaultTenants, "Customers");
                AssertIdenticalColumnsAcrossSchemas(DefaultTenants, "Orders");

                // Migration tracking per (template, schema): one row per tenant for SeedTenantMarker,
                // plus one shared row for SeedLookup.
                AssertMigrationTracked(SharedTemplate, "", "MigrationScripts/Before/SeedLookup.sql");
                foreach (var tenant in DefaultTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant, "MigrationScripts/Before/SeedTenantMarker.sql");

                // Per-iteration marker INSERT landed.
                foreach (var tenant in DefaultTenants)
                {
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[Customers] WITH (NOLOCK) WHERE Marker = N'{tenant}_marker'");
                    Assert.That(count, Is.EqualTo(1), $"Tenant '{tenant}' should have its per-iteration marker row.");
                }

                // The Setup slot ran once in each tenant schema, with that schema's token.
                foreach (var tenant in DefaultTenants)
                    Assert.That(ScalarCount($"SELECT COUNT(*) FROM [{tenant}].[SetupMarker]"), Is.EqualTo(1),
                        $"Setup runs once per schema: tenant '{tenant}'");
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Capture the ordered sequence of Info() messages and assert that:
                //   Shared "Quenching Template" precedes every per-iteration "[Schema: ...] Begin Quench".
                var infos = _progressLog.ReceivedCalls()
                    .Where(c => c.GetMethodInfo().Name == nameof(ILog.Info))
                    .Select(c => (string)c.GetArguments()[0])
                    .ToList();

                var sharedIdx = infos.FindIndex(s => s == $"Quenching Template: {SharedTemplate}");
                var tenantBodyIdx = infos.FindIndex(s => s == $"Quenching Template: {TenantBodyTemplate}");
                Assert.That(sharedIdx, Is.GreaterThanOrEqualTo(0), "Shared template start log not found");
                Assert.That(tenantBodyIdx, Is.GreaterThan(sharedIdx),
                    "TenantBody must start AFTER Shared starts (TemplateOrder).");

                // Shared has no schema iterations, so it produces a non-[Schema: ] Successfully Quenched
                // BEFORE any tenant iteration begins. Concretely: the dbo-level (no [Schema:]) line
                // for the Shared template must precede every [Schema: <tenant>] iteration.
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

            // Insert a poisoned 'dbo' tenant row so discovery returns a reserved name.
            // Real product code's reserved-name guard should reject this and abort the template.
            ExecuteOnMainDb("INSERT INTO dbo.SchemaTemplateTenants ([Name]) VALUES (N'dbo')");

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                // Discovery failure should surface an error mentioning the reserved name and exit 2.
                _progressLog.Received().Error(Arg.Is<string>(s =>
                    s.Contains("Schema discovery FAILED") &&
                    s.Contains("reserved schema name 'dbo'")));
                _environment.Received().Exit(2);

                // Other discovered schemas in the same run must NOT be deployed. Concretely: no tenant
                // schema has Customers because TenantBody never dispatched (slice-3 abort-on-discovery).
                foreach (var tenant in DefaultTenants)
                {
                    var customers = ScalarCount(
                        $"SELECT COUNT(*) FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{tenant}' AND t.name = 'Customers'");
                    Assert.That(customers, Is.EqualTo(0),
                        $"Tenant '{tenant}' must not have Customers — TenantBody must not have dispatched after reserved-name discovery failure.");
                }
            }
            finally
            {
                ExecuteOnMainDb("DELETE FROM dbo.SchemaTemplateTenants WHERE [Name] = N'dbo'");
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Per_Iteration_Query_Token_Resolves_Differently_Per_Tenant_And_Lands_In_Procedure_Body()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Each tenant's GetTenantLabel procedure body should contain the tenant-specific
                // resolved {{TenantLabel}} value: '<tenant>_label'. Read sys.sql_modules.definition
                // and assert.
                foreach (var tenant in DefaultTenants)
                {
                    var procDefinition = ScalarString(
                        $"SELECT m.definition FROM sys.sql_modules m INNER JOIN sys.objects o ON m.object_id = o.object_id INNER JOIN sys.schemas s ON o.schema_id = s.schema_id WHERE s.name = '{tenant}' AND o.name = 'GetTenantLabel'");
                    Assert.That(procDefinition, Is.Not.Null.And.Not.Empty,
                        $"Tenant '{tenant}' should have GetTenantLabel procedure deployed.");
                    Assert.That(procDefinition, Contains.Substring($"{tenant}_label"),
                        $"Tenant '{tenant}' procedure body should carry its iteration-resolved TenantLabel.");

                    // And the other tenants' labels must NOT be present in this tenant's procedure body.
                    foreach (var otherTenant in DefaultTenants.Where(t => t != tenant))
                        Assert.That(procDefinition, Does.Not.Contain($"{otherTenant}_label"),
                            $"Tenant '{tenant}' procedure body must not contain '{otherTenant}_label' — token re-resolution per iteration is the contract.");
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                // First quench creates schemas and tracking rows.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Verify each tenant has its own tracking row.
                foreach (var tenant in DefaultTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant, "MigrationScripts/Before/SeedTenantMarker.sql");
                AssertMigrationTracked(SharedTemplate, "", "MigrationScripts/Before/SeedLookup.sql");

                // Mutate the per-tenant marker so we can prove SeedTenantMarker is NOT re-run.
                foreach (var tenant in DefaultTenants)
                    ExecuteOnMainDb($"UPDATE [{tenant}].[Customers] SET Marker = N'mutated' WHERE CustomerID = 1");

                // Second quench: same package, no changes — every tenant's SeedTenantMarker should
                // already be in the tracking table and skipped.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Markers should still be 'mutated' — SeedTenantMarker must have been skipped.
                foreach (var tenant in DefaultTenants)
                {
                    var marker = ScalarString(
                        $"SELECT Marker FROM [{tenant}].[Customers] WITH (NOLOCK) WHERE CustomerID = 1");
                    Assert.That(marker, Is.EqualTo("mutated"),
                        $"Tenant '{tenant}': second quench must skip the migration — marker should still be 'mutated'.");
                }

                // Tracking table still has exactly one row per (TenantBody, tenant).
                foreach (var tenant in DefaultTenants)
                {
                    var trackingCount = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}' AND ScriptPath = 'MigrationScripts/Before/SeedTenantMarker.sql'");
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
        // result sets, and the emitted MERGE referenced [{{SchemaName}}].[Lookups] — silently
        // broken end-to-end (either MERGE syntax error or no rows landed).
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Each tenant's Lookups table must contain the seeded rows.
                foreach (var tenant in DefaultTenants)
                {
                    AssertTableExists(tenant, "Lookups");
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[Lookups] WITH (NOLOCK)");
                    Assert.That(count, Is.EqualTo(3),
                        $"Tenant '{tenant}' must have all three Lookups rows delivered.");

                    var codes = QueryRows(
                        $"SELECT [Code] FROM [{tenant}].[Lookups] WITH (NOLOCK) ORDER BY [LookupID]");
                    Assert.That(codes, Is.EqualTo(new[] { "ALPHA", "BETA", "GAMMA" }),
                        $"Tenant '{tenant}' Lookups codes must match the content file.");
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Per-tenant: FK_Orders_Customers (same iteration) AND FK_Orders_Lookup (cross-schema to dbo).
                foreach (var tenant in DefaultTenants)
                {
                    var sameIterationFk = ScalarCount($@"
SELECT COUNT(*) FROM sys.foreign_keys fk
  INNER JOIN sys.tables ft ON fk.parent_object_id = ft.object_id
  INNER JOIN sys.schemas fs ON ft.schema_id = fs.schema_id
  INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
  INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
WHERE fs.name = '{tenant}' AND ft.name = 'Orders' AND fk.name = 'FK_Orders_Customers'
  AND rs.name = '{tenant}' AND rt.name = 'Customers'");
                    Assert.That(sameIterationFk, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have FK_Orders_Customers same-iteration FK.");

                    var crossSchemaFk = ScalarCount($@"
SELECT COUNT(*) FROM sys.foreign_keys fk
  INNER JOIN sys.tables ft ON fk.parent_object_id = ft.object_id
  INNER JOIN sys.schemas fs ON ft.schema_id = fs.schema_id
  INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
  INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
WHERE fs.name = '{tenant}' AND ft.name = 'Orders' AND fk.name = 'FK_Orders_Lookup'
  AND rs.name = 'dbo' AND rt.name = 'Lookup'");
                    Assert.That(crossSchemaFk, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have FK_Orders_Lookup cross-schema FK pointing at dbo.Lookup.");
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // After quench #1, each tenant should have exactly one audit row.
                foreach (var tenant in DefaultTenants)
                {
                    var rowsAfterFirst = ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.SharedAudit WHERE [Tenant] = N'{tenant}' AND [Note] = N'always-touched'");
                    Assert.That(rowsAfterFirst, Is.EqualTo(1),
                        $"After quench #1 tenant '{tenant}' should have exactly one [ALWAYS] audit row.");
                }

                // The [ALWAYS] script must NOT show up in CompletedMigrationScripts — that's the
                // whole point of the [ALWAYS] tag (design §6.7).
                foreach (var tenant in DefaultTenants)
                {
                    var tracked = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}' AND ScriptPath LIKE '%TouchAudit%'");
                    Assert.That(tracked, Is.EqualTo(0),
                        $"Tenant '{tenant}': [ALWAYS] script must not appear in CompletedMigrationScripts.");
                }

                // Second quench: same tenants, no changes. Each tenant should now have TWO rows.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var rowsAfterSecond = ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.SharedAudit WHERE [Tenant] = N'{tenant}' AND [Note] = N'always-touched'");
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
        // "execute" becomes "log the SQL that would have run." Real deploy first so tables
        // exist; then re-quench with WhatIfONLY=true and assert the second pass is read-only.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                // First quench: real deploy.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Capture per-tenant state BEFORE WhatIf.
                var markerBefore = DefaultTenants
                    .ToDictionary(t => t, t => ScalarString($"SELECT Marker FROM [{t}].[Customers] WITH (NOLOCK) WHERE CustomerID = 1"));
                var trackingRowsBefore = DefaultTenants
                    .ToDictionary(t => t, t => ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{t}'"));
                var alwaysRowsBefore = DefaultTenants
                    .ToDictionary(t => t, t => ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.SharedAudit WITH (NOLOCK) WHERE [Tenant] = N'{t}' AND [Note] = N'always-touched'"));

                _progressLog.ClearReceivedCalls();

                // Second quench: WhatIf mode.
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
                foreach (var tenant in DefaultTenants)
                {
                    var markerAfter = ScalarString($"SELECT Marker FROM [{tenant}].[Customers] WITH (NOLOCK) WHERE CustomerID = 1");
                    Assert.That(markerAfter, Is.EqualTo(markerBefore[tenant]),
                        $"Tenant '{tenant}': WhatIf must not modify the customer marker.");

                    var trackingAfter = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}'");
                    Assert.That(trackingAfter, Is.EqualTo(trackingRowsBefore[tenant]),
                        $"Tenant '{tenant}': WhatIf must not change tracking row count.");

                    var alwaysAfter = ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.SharedAudit WITH (NOLOCK) WHERE [Tenant] = N'{tenant}' AND [Note] = N'always-touched'");
                    Assert.That(alwaysAfter, Is.EqualTo(alwaysRowsBefore[tenant]),
                        $"Tenant '{tenant}': WhatIf must not execute the [ALWAYS] script.");
                }
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
    public void WhatIf_With_Schema_Template_Reports_One_WouldApply_Entry_Per_Tenant_For_Object_Script()
    {
        // Regression coverage for the report's wouldApply[] count on a multi-scope schema-template
        // WhatIf run — the exact surface the (Scope, Script) assembler dedup and the DatabaseQuench
        // HasBeenQuenched-gated WhatIfLogScripts were both written to fix (over-count), and the exact
        // surface a cross-scope HasBeenQuenched leak would silently under-count (SqlScript.Clone()
        // never copies HasBeenQuenched, and PrepareIterationContent clones fresh SqlScript instances
        // per schema-template iteration — see DatabaseQuench.cs PrepareIterationContent — so there is
        // no shared object-reference path between tenants for this to leak across). The engine has no
        // prior end-to-end test asserting the wouldApply COUNT at all; every other WhatIf test here
        // only asserts on console log lines. GetTenantLabel.sql (TenantBody/Procedures, QuenchSlot
        // "Objects") is an Object-slot script, so it is reachable through BOTH ObjectFolders and
        // AfterTablesObjectFolders (Template.cs AfterTablesObjectFolders deliberately includes the
        // Objects slot) — the exact overlap that produces a same-scope duplicate raw WhatIfRun that
        // only the assembler's string-keyed dedup (not DatabaseQuench's object-reference-keyed skip)
        // can collapse.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                // First quench: real deploy so tenant schemas/tables/procs exist (mirrors the other
                // WhatIf tests in this fixture — WhatIf's discovery script depends on prior state).
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Second quench: WhatIf mode — the run whose summary we inspect.
                config["WhatIfONLY"] = "true";
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                var jsonPath = Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json");
                Assert.That(File.Exists(jsonPath), Is.True, $"Expected deployment summary JSON at {jsonPath}");

                var json = JObject.Parse(File.ReadAllText(jsonPath));
                Assert.That(json.SelectToken("run.mode")?.Value<string>(), Is.EqualTo("WhatIf"));

                var wouldApply = (JArray)json.SelectToken("whatIf.wouldApply");
                Assert.That(wouldApply, Is.Not.Null);

                var tenantLabelEntries = wouldApply
                    .Where(e => (e["script"]?.Value<string>() ?? "").EndsWith(
                        $"Procedures{Path.DirectorySeparatorChar}GetTenantLabel.sql"))
                    .ToList();

                // One entry per tenant scope — not one collapsed entry (cross-scope HasBeenQuenched
                // suppression) and not more than one per tenant (same-scope duplicate logging).
                Assert.That(tenantLabelEntries.Count, Is.EqualTo(DefaultTenants.Length),
                    "wouldApply should report GetTenantLabel.sql exactly once per tenant scope — " +
                    $"got {tenantLabelEntries.Count} for {DefaultTenants.Length} tenants. Fewer than " +
                    $"{DefaultTenants.Length} would indicate cross-scope HasBeenQuenched suppression; " +
                    "more would indicate same-scope duplicate WhatIfLogScripts passes escaping dedup.");

                var expectedScopes = DefaultTenants
                    .Select(t => $"[{_server}].[{_mainDb}] [Schema: {t}]")
                    .ToList();
                var actualScopes = tenantLabelEntries.Select(e => e["scope"]?.Value<string>()).ToList();

                Assert.That(actualScopes, Is.EquivalentTo(expectedScopes),
                    "each tenant's wouldApply entry must carry that tenant's own [Schema: ...] scope, " +
                    "proving the entries are genuinely per-scope and not one scope's entry duplicated.");
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
        // Post-slice-8 cleanup (Commit C): the WhatIf + Target.Schemas composition. A targeted
        // WhatIf run must produce iteration log output ONLY for the filtered tenant (no [Schema:
        // tenant_beta] / [Schema: tenant_globex] WhatIf chatter), and must leave every tenant's
        // state untouched — same read-only guarantee as the plain WhatIf path. The two surfaces
        // are independent (filter + dry-run); this test pins the composition.
        const string targetTenant = "tenant_acme";
        var siblingTenants = new[] { "tenant_beta", "tenant_globex" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                // First quench: real deploy so all three tenants exist + tracking rows land.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Snapshot per-tenant state BEFORE the targeted WhatIf so we can prove every
                // tenant (including the targeted one) is read-only across the filtered run.
                var markerBefore = DefaultTenants.ToDictionary(t => t,
                    t => ScalarString($"SELECT Marker FROM [{t}].[Customers] WITH (NOLOCK) WHERE CustomerID = 1"));
                var trackingBefore = DefaultTenants.ToDictionary(t => t,
                    t => ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{t}'"));
                var alwaysAuditBefore = DefaultTenants.ToDictionary(t => t,
                    t => ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.SharedAudit WITH (NOLOCK) WHERE [Tenant] = N'{t}' AND [Note] = N'always-touched'"));

                _progressLog.ClearReceivedCalls();

                // Second quench: WhatIfONLY + Target.Schemas scoped to a single tenant.
                config["WhatIfONLY"] = "true";
                config["Target:Schemas:0"] = targetTenant;
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // The target tenant must run (under WhatIf) — its Successfully Quenched line
                // appears, and its WhatIf Before-scripts line appears.
                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: {targetTenant}] Successfully Quenched");
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"[Schema: {targetTenant}]") && s.Contains("[WhatIf] Before database scripts:")));

                // Sibling tenants must produce NO iteration log lines — the filter rules them out
                // before any work unit dispatches, so neither WhatIf chatter nor a Successfully
                // Quenched line should appear for them.
                foreach (var sibling in siblingTenants)
                {
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {sibling}] Successfully Quenched");
                    _progressLog.DidNotReceive().Info(Arg.Is<string>(s =>
                        s.Contains($"[Schema: {sibling}]") && s.Contains("[WhatIf]")));
                    _progressLog.DidNotReceive().Info(Arg.Is<string>(s =>
                        s.Contains($"[Schema: {sibling}]") && s.Contains("Begin Quench")));
                }

                // No tenant's state may change — WhatIf is read-only regardless of filter scope.
                foreach (var tenant in DefaultTenants)
                {
                    var markerAfter = ScalarString(
                        $"SELECT Marker FROM [{tenant}].[Customers] WITH (NOLOCK) WHERE CustomerID = 1");
                    Assert.That(markerAfter, Is.EqualTo(markerBefore[tenant]),
                        $"Tenant '{tenant}': WhatIf+Target.Schemas must not modify the customer marker.");

                    var trackingAfter = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = '{tenant}'");
                    Assert.That(trackingAfter, Is.EqualTo(trackingBefore[tenant]),
                        $"Tenant '{tenant}': WhatIf+Target.Schemas must not change tracking row count.");

                    var alwaysAuditAfter = ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.SharedAudit WITH (NOLOCK) WHERE [Tenant] = N'{tenant}' AND [Note] = N'always-touched'");
                    Assert.That(alwaysAuditAfter, Is.EqualTo(alwaysAuditBefore[tenant]),
                        $"Tenant '{tenant}': WhatIf+Target.Schemas must not execute the [ALWAYS] script.");
                }
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
        // Post-slice-8 cleanup (Commit C): the customer-facing form of the slice-3 audit B1 fix
        // (ProductOwnership scoped per template + schema). Deploy 3 tenants, then add a stray
        // table to ONE tenant's schema (a table SchemaSmith doesn't own — i.e., a table not in
        // the package). Re-quench with DropTablesRemovedFromProduct=true. The drop pass scoping
        // must be per-iterating-tenant: every tenant's iteration evaluates its OWN schema's
        // owned-by-product tables against the package. A stray table outside SchemaSmith's
        // ownership is left alone everywhere; the package's per-tenant tables must remain
        // intact in every tenant after the targeted drop pass runs across the fan-out.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                // Quench #1: full multi-tenant deploy. Every tenant gets Customers + tracking.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                foreach (var tenant in DefaultTenants)
                    AssertTableExists(tenant, "Customers");

                // Quench #2: enable DropTablesRemovedFromProduct. The package still defines
                // Customers for every tenant, so each tenant's Customers must SURVIVE the drop
                // pass (the audit B1 regression would have caused sibling iterations to see
                // a different tenant's Customers and drop it).
                config["DropTablesRemovedFromProduct"] = "true";
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    AssertTableExists(tenant, "Customers");
                    // Sibling tenants' marker rows untouched — the migration is run-once tracked
                    // and the drop pass on tenant_acme's iteration must not have crossed into
                    // sibling tenants' schemas.
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[Customers] WITH (NOLOCK) WHERE Marker = N'{tenant}_marker'");
                    Assert.That(count, Is.EqualTo(1),
                        $"Tenant '{tenant}': per-iteration Customers row must survive the cross-tenant drop pass.");
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
        // Design §6.8 scenario D: a tenant removed from discovery has its tracking rows
        // retained (harmless). Re-adding the tenant later finds the rows intact and run-once
        // migrations correctly skip. Concretely:
        //   1. Quench with {acme, beta, globex} — beta's SeedTenantMarker tracked.
        //   2. Remove beta from dbo.SchemaTemplateTenants. Mutate beta's Marker row directly.
        //   3. Quench again — beta is NOT in discovery → no iteration → no mutation.
        //   4. Re-add beta. Quench again — beta's tracking row still exists → migration
        //      skips → the mutated marker is preserved.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                // Quench 1: deploys all three tenants and tracks SeedTenantMarker per tenant.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                AssertMigrationTracked(TenantBodyTemplate, "tenant_beta", "MigrationScripts/Before/SeedTenantMarker.sql");

                // Mutate beta's marker so we can prove the migration is NOT re-run after the
                // tenant returns.
                ExecuteOnMainDb("UPDATE [tenant_beta].[Customers] SET Marker = N'mutated' WHERE CustomerID = 1");

                // Offboard beta from discovery.
                ExecuteOnMainDb("DELETE FROM dbo.SchemaTemplateTenants WHERE [Name] = N'tenant_beta'");

                // Quench 2: beta is not in discovery, so no work unit is dispatched for it.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // The beta-iteration log line for "Successfully Quenched" must NOT appear in
                // quench 2 — beta was offboarded.
                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_beta] Successfully Quenched");

                // Beta's mutated marker should still be there (no run-once migration touched it).
                var markerAfterOffboard = ScalarString(
                    "SELECT Marker FROM [tenant_beta].[Customers] WITH (NOLOCK) WHERE CustomerID = 1");
                Assert.That(markerAfterOffboard, Is.EqualTo("mutated"),
                    "Offboarded tenant must not be re-touched by any subsequent quench.");

                // Beta's tracking row must still exist — it's the seed for the re-onboarding case.
                AssertMigrationTracked(TenantBodyTemplate, "tenant_beta", "MigrationScripts/Before/SeedTenantMarker.sql");

                // Re-onboard beta.
                ExecuteOnMainDb("INSERT INTO dbo.SchemaTemplateTenants ([Name]) VALUES (N'tenant_beta')");

                // Quench 3: beta is back. Its tracking row exists, so SeedTenantMarker must skip.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_beta] Successfully Quenched");

                // The mutated marker should still be there — SeedTenantMarker was correctly skipped
                // for the re-onboarded tenant. Slice 2 migration tracking earns its keep here.
                var markerAfterReOnboard = ScalarString(
                    "SELECT Marker FROM [tenant_beta].[Customers] WITH (NOLOCK) WHERE CustomerID = 1");
                Assert.That(markerAfterReOnboard, Is.EqualTo("mutated"),
                    "Re-onboarded tenant must skip already-tracked run-once migrations.");

                // Still exactly one tracking row for SeedTenantMarker on tenant_beta — no duplicates.
                var betaTrackingCount = ScalarCount(
                    $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND schema_name = 'tenant_beta' AND ScriptPath = 'MigrationScripts/Before/SeedTenantMarker.sql'");
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
    public void Schema_Template_With_No_Tables_Only_Before_And_After_Scripts_Iterates_Per_Schema()
    {
        // Design surface: a schema template whose ScriptFolders include only Before/After
        // (no Tables, no Procedures, no engine-generated DDL) must still iterate per
        // discovered schema, substitute {{SchemaName}}, and track the run-once Before
        // migration per (template, schema). Uses a dedicated fixture so the existing
        // multi-template SchemaTemplateProduct keeps its full-pipeline shape.
        const string noTablesProduct = "SchemaTemplateNoTablesProduct";
        var tenants = new[] { "tenantops_a", "tenantops_b" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            // Clean any prior state + create the per-tenant schemas + audit tables (the
            // template itself can't create tables — it has no Tables folder).
            Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(noTablesProduct);
            using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 0;
                cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{noTablesProduct}';";
                cmd.ExecuteNonQuery();
                foreach (var tenant in tenants)
                {
                    cmd.CommandText = $@"
IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');
IF OBJECT_ID('[{tenant}].[Audit]', 'U') IS NULL
    CREATE TABLE [{tenant}].[Audit] ([AuditID] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Audit_{tenant}] PRIMARY KEY, [Note] NVARCHAR(256) NOT NULL);";
                    cmd.ExecuteNonQuery();
                }
                conn.Close();
            }

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", noTablesProduct);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in tenants)
                {
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                    // Both Before and After scripts must have landed per tenant.
                    var beforeRow = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[Audit] WITH (NOLOCK) WHERE [Note] = N'no-tables-marker-{tenant}'");
                    Assert.That(beforeRow, Is.EqualTo(1),
                        $"Tenant '{tenant}': Before-slot script must run with {{{{SchemaName}}}} substituted.");

                    var afterRow = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[Audit] WITH (NOLOCK) WHERE [Note] = N'after-{tenant}'");
                    Assert.That(afterRow, Is.EqualTo(1),
                        $"Tenant '{tenant}': After-slot script must run with {{{{SchemaName}}}} substituted.");

                    // Run-once Before migration must be tracked per (template, schema).
                    var trackedBefore = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{noTablesProduct}' AND template_name = 'TenantOps' AND schema_name = '{tenant}' AND ScriptPath = 'Before Scripts/SeedMarker.sql'");
                    Assert.That(trackedBefore, Is.EqualTo(1),
                        $"Tenant '{tenant}': Before-slot migration must be tracked.");
                }
            }
            finally
            {
                using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
                {
                    conn.Open();
                    conn.ChangeDatabase(_mainDb);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 0;
                    foreach (var tenant in tenants)
                    {
                        cmd.CommandText = $@"
IF OBJECT_ID('[{tenant}].[Audit]', 'U') IS NOT NULL DROP TABLE [{tenant}].[Audit];
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
                        cmd.ExecuteNonQuery();
                    }
                    cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{noTablesProduct}';";
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Two_Schema_Templates_Sharing_Script_Filename_Track_Independently_Per_Template()
    {
        // Slice 2 strict template_name on writes/deletes: two schema templates with the SAME
        // script relative path must produce independent tracking rows. A second quench must
        // skip BOTH (each via its own row), and the audit table must show both INSERTs landed
        // (not just one — the lookup must not shadow-skip the second template's run).
        const string dupProduct = "SchemaTemplateDupScriptProduct";
        var tenants = new[] { "dupset_x", "dupset_y" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(dupProduct);
            using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 0;
                cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{dupProduct}';";
                cmd.ExecuteNonQuery();
                foreach (var tenant in tenants)
                {
                    cmd.CommandText = $@"
IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');
IF OBJECT_ID('[{tenant}].[DupAudit]', 'U') IS NULL
    CREATE TABLE [{tenant}].[DupAudit] ([DupID] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DupAudit_{tenant}] PRIMARY KEY, [Source] NVARCHAR(256) NOT NULL);";
                    cmd.ExecuteNonQuery();
                }
                conn.Close();
            }

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", dupProduct);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Each tenant's DupAudit must have BOTH the Alpha and Beta INSERT — independent
                // template_name tracking must not shadow the second template's run.
                foreach (var tenant in tenants)
                {
                    var alphaRows = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[DupAudit] WITH (NOLOCK) WHERE [Source] = N'DupAlpha-{tenant}'");
                    Assert.That(alphaRows, Is.EqualTo(1),
                        $"Tenant '{tenant}': DupAlpha's Before/Seed.sql must run.");

                    var betaRows = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[DupAudit] WITH (NOLOCK) WHERE [Source] = N'DupBeta-{tenant}'");
                    Assert.That(betaRows, Is.EqualTo(1),
                        $"Tenant '{tenant}': DupBeta's Before/Seed.sql must run despite sharing the same relative path as DupAlpha's.");

                    // Two distinct tracking rows: one per (template, schema, scriptPath).
                    var trackingRows = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{dupProduct}' AND schema_name = '{tenant}' AND ScriptPath = 'Before Scripts/Seed.sql'");
                    Assert.That(trackingRows, Is.EqualTo(2),
                        $"Tenant '{tenant}': must have TWO tracking rows for 'Before Scripts/Seed.sql' — one per template_name.");

                    var alphaTracked = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{dupProduct}' AND template_name = 'DupAlpha' AND schema_name = '{tenant}' AND ScriptPath = 'Before Scripts/Seed.sql'");
                    Assert.That(alphaTracked, Is.EqualTo(1), $"DupAlpha tracking row must exist for tenant '{tenant}'.");

                    var betaTracked = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{dupProduct}' AND template_name = 'DupBeta' AND schema_name = '{tenant}' AND ScriptPath = 'Before Scripts/Seed.sql'");
                    Assert.That(betaTracked, Is.EqualTo(1), $"DupBeta tracking row must exist for tenant '{tenant}'.");
                }

                // Second quench: both should skip — neither row shadow-pruning the other.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in tenants)
                {
                    var totalRows = ScalarCount(
                        $"SELECT COUNT(*) FROM [{tenant}].[DupAudit] WITH (NOLOCK)");
                    Assert.That(totalRows, Is.EqualTo(2),
                        $"Tenant '{tenant}': second quench must skip both seed scripts — total rows stay at 2.");
                }
            }
            finally
            {
                using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
                {
                    conn.Open();
                    conn.ChangeDatabase(_mainDb);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 0;
                    foreach (var tenant in tenants)
                    {
                        cmd.CommandText = $@"
IF OBJECT_ID('[{tenant}].[DupAudit]', 'U') IS NOT NULL DROP TABLE [{tenant}].[DupAudit];
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
                        cmd.ExecuteNonQuery();
                    }
                    cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{dupProduct}';";
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Schema_Template_With_IndexOnlyTableQuenches_True_Adds_Missing_Index_Without_Creating_Table()
    {
        // Combination test: IndexOnlyTableQuenches=true on a schema template skips
        // MissingTableAndColumnQuench / ModifiedTableQuench but still runs IndexOnlyQuench
        // with {{SchemaName}}-substituted IterationTableSchema. The test pre-creates the
        // tenant table (without the IX_Widgets_Label index), runs a quench, and asserts
        // the index now exists but the table wasn't recreated.
        const string idxOnlyProduct = "SchemaTemplateIndexOnlyProduct";
        var tenants = new[] { "idxonly_one" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(idxOnlyProduct);
            using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 0;
                cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{idxOnlyProduct}';";
                cmd.ExecuteNonQuery();
                foreach (var tenant in tenants)
                {
                    cmd.CommandText = $@"
IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');
IF OBJECT_ID('[{tenant}].[Widgets]', 'U') IS NULL
    CREATE TABLE [{tenant}].[Widgets] (
        [WidgetID] INT NOT NULL CONSTRAINT [PK_Widgets] PRIMARY KEY,
        [Label] NVARCHAR(64) NOT NULL);";
                    cmd.ExecuteNonQuery();
                }
                conn.Close();
            }

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", idxOnlyProduct);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in tenants)
                {
                    // The missing IX_Widgets_Label index must now exist.
                    var ixCount = ScalarCount($@"
SELECT COUNT(*) FROM sys.indexes i
  INNER JOIN sys.tables t ON i.object_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND t.name = 'Widgets' AND i.name = 'IX_Widgets_Label'");
                    Assert.That(ixCount, Is.EqualTo(1),
                        $"Tenant '{tenant}': IndexOnlyQuench must add the missing IX_Widgets_Label index.");

                    // Table still exists. PK count is exactly 1 — the engine doesn't
                    // add a second PK on top of the pre-existing one.
                    var pkCount = ScalarCount($@"
SELECT COUNT(*) FROM sys.key_constraints kc
  INNER JOIN sys.tables t ON kc.parent_object_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND t.name = 'Widgets' AND kc.type = 'PK'");
                    Assert.That(pkCount, Is.EqualTo(1),
                        $"Tenant '{tenant}': exactly one PK on Widgets after quench (engine did not add a duplicate).");
                }
            }
            finally
            {
                using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
                {
                    conn.Open();
                    conn.ChangeDatabase(_mainDb);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 0;
                    foreach (var tenant in tenants)
                    {
                        cmd.CommandText = $@"
IF OBJECT_ID('[{tenant}].[Widgets]', 'U') IS NOT NULL DROP TABLE [{tenant}].[Widgets];
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
                        cmd.ExecuteNonQuery();
                    }
                    cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{idxOnlyProduct}';";
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Indexed_View_Per_Tenant_Survives_Second_Quench_No_Cross_Tenant_Drops()
    {
        // Slice-3 audit B5 regression: SQL Server SchemaSmith.IndexedViewQuench previously
        // read existing indexed views GLOBALLY across all schemas owned by the product, then
        // dropped any view not in this iteration's substituted @IndexedViewSchema JSON. Under
        // parallel schema-template execution, tenant_acme's iteration saw tenant_beta's and
        // tenant_globex's indexed views in @ExistingViews and tried to drop them; sibling
        // iterations raced. Fix scopes the existing-views lookup to (@TemplateName, @SchemaName)
        // when @SchemaName is non-empty (regular templates pass '' and fall through to
        // today's all-schemas behavior).
        //
        // Test pattern mirrors PG's MaterializedView regression: deploy 3 tenants whose
        // TenantBody template carries an indexed view; quench twice; assert every tenant
        // still owns its IV after the second pass.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var ivCount = ScalarCount($@"
SELECT COUNT(*) FROM sys.views v
  INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND v.name = 'vw_CustomerCount'
  AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1");
                    Assert.That(ivCount, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have its vw_CustomerCount indexed view after first quench.");
                }

                // Second quench: bug-trigger path. Pre-fix, tenant_acme's iteration would see
                // tenant_beta's and tenant_globex's vw_CustomerCount as "removed from product"
                // and drop them (and sibling iterations would race trying to drop tenant_acme's).
                // Post-fix, each iteration's @ExistingViews is scoped to its own schema.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                {
                    var ivCount = ScalarCount($@"
SELECT COUNT(*) FROM sys.views v
  INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND v.name = 'vw_CustomerCount'
  AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1");
                    Assert.That(ivCount, Is.EqualTo(1),
                        $"After second quench tenant '{tenant}' must still have vw_CustomerCount — sibling iterations must not have dropped it.");
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

            using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 0;
                // Counter table — recreated empty so prior runs in the same fixture lifecycle
                // don't pollute this run's row counts.
                cmd.CommandText = @"
IF OBJECT_ID('dbo.TokenCallCounter', 'U') IS NOT NULL DROP TABLE dbo.TokenCallCounter;
CREATE TABLE dbo.TokenCallCounter (
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TokenCallCounter PRIMARY KEY,
    TokenKind NVARCHAR(32) NOT NULL,
    IterationSchema NVARCHAR(128) NOT NULL);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{cacheProduct}';";
                cmd.ExecuteNonQuery();
                foreach (var tenant in tenants)
                {
                    cmd.CommandText = $"IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');";
                    cmd.ExecuteNonQuery();
                }
                conn.Close();
            }

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", cacheProduct);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // The per-DB token must execute exactly once across all 3 iterations — the first
                // iteration's resolution populates the cache; iterations 2 + 3 read from it and
                // skip the connection round-trip (no counter row inserted).
                var perDbCalls = ScalarCount(
                    "SELECT COUNT(*) FROM dbo.TokenCallCounter WHERE TokenKind = 'PerDb'");
                Assert.That(perDbCalls, Is.EqualTo(1),
                    $"Per-DB query token must execute exactly once across {tenants.Length} iterations (cached). " +
                    "If this is N, the cache is not engaged and the token is re-running per iteration.");

                // The iteration-scoped token must execute once per iteration (3 times total).
                var iterCalls = ScalarCount(
                    "SELECT COUNT(*) FROM dbo.TokenCallCounter WHERE TokenKind = 'Iteration'");
                Assert.That(iterCalls, Is.EqualTo(tenants.Length),
                    $"Iteration-scoped query token must execute once per iteration ({tenants.Length} total).");

                // Per-iteration token must carry each tenant's schema in its body — proves caching
                // didn't accidentally swap iteration-scoped values across iterations.
                foreach (var tenant in tenants)
                {
                    var tenantIterRows = ScalarCount(
                        $"SELECT COUNT(*) FROM dbo.TokenCallCounter WHERE TokenKind = 'Iteration' AND IterationSchema = N'{tenant}'");
                    Assert.That(tenantIterRows, Is.EqualTo(1),
                        $"Iteration token for tenant '{tenant}' must have produced exactly one counter row.");
                }

                // The procedure body in each tenant schema must carry the per-DB token's
                // resolved literal — proves the cached value was substituted into the script,
                // not just shorted out of the connection round-trip.
                foreach (var tenant in tenants)
                {
                    var procBody = ScalarString(
                        $"SELECT m.definition FROM sys.sql_modules m INNER JOIN sys.objects o ON m.object_id = o.object_id INNER JOIN sys.schemas s ON o.schema_id = s.schema_id WHERE s.name = '{tenant}' AND o.name = 'GetTokens'");
                    Assert.That(procBody, Does.Contain("perdb_value"),
                        $"Tenant '{tenant}' procedure must carry the resolved per-DB token literal — cache must merge cached value back into the iteration's substitution list.");
                    Assert.That(procBody, Does.Contain($"iter_value_{tenant}"),
                        $"Tenant '{tenant}' procedure must carry its own iteration-scoped token value (proves no cross-iteration leakage).");
                }
            }
            finally
            {
                using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
                {
                    conn.Open();
                    conn.ChangeDatabase(_mainDb);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 0;
                    foreach (var tenant in tenants)
                    {
                        cmd.CommandText = $@"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + 'DROP PROCEDURE [' + s.name + '].[' + o.name + '];' + CHAR(10)
  FROM sys.objects o
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND o.type = 'P';
IF @sql <> '' EXEC sp_executesql @sql;
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
                        cmd.ExecuteNonQuery();
                    }
                    cmd.CommandText = @$"
IF OBJECT_ID('dbo.TokenCallCounter', 'U') IS NOT NULL DROP TABLE dbo.TokenCallCounter;
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{cacheProduct}';";
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private void SetupSharedMocks()
    {
        // Re-assert SqlServer's Target:* on the shared config before the quench reads it live: a sibling
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
        // disk alone leaves stale cache entries that skip checkpoint-tracked steps and leave
        // session-scoped temp tables uncreated).
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);
    }

    private void ResetTrackingAndCreateTenantSchemas(IEnumerable<string> tenants)
    {
        ClearCheckpointsForProduct();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        // Reset migration tracking + shared content from prior runs in this fixture's lifetime.
        cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}';
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NOT NULL DELETE FROM dbo.SchemaTemplateTenants;
IF OBJECT_ID('dbo.Lookup', 'U') IS NOT NULL DELETE FROM dbo.Lookup;";
        cmd.ExecuteNonQuery();

        // Drop any leftover tenant schemas from a previously aborted run.
        DropTenantSchemasInternal(cmd, tenants);

        // Pre-create each tenant schema (slice 3 assumes schemas exist — slice 4 adds CreateSchemaIfMissing).
        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }

        // The discovery script reads dbo.SchemaTemplateTenants — that table is created by the Shared template, but
        // it must already exist on the FIRST quench (TenantBody's DatabaseIdentificationScript runs
        // before Shared's tables are created if we left this to the fixture only). Create it
        // pre-emptively here so the test bootstraps cleanly; the Shared template's own CREATE-IF-MISSING
        // semantics will tolerate the pre-existing table.
        cmd.CommandText = @"
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NULL
    CREATE TABLE dbo.SchemaTemplateTenants ([Name] NVARCHAR(128) NOT NULL CONSTRAINT PK_SchemaTemplateTenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        // Insert tenant rows.
        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO dbo.SchemaTemplateTenants ([Name]) VALUES (N'{tenant}');";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    private void DropTenantSchemas(IEnumerable<string> tenants)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        DropTenantSchemasInternal(cmd, tenants);

        // Reset shared content as a courtesy so the next test starts clean.
        cmd.CommandText = @$"
IF OBJECT_ID('dbo.SharedAudit', 'U') IS NOT NULL DROP TABLE dbo.SharedAudit;
IF OBJECT_ID('dbo.Lookup', 'U') IS NOT NULL DROP TABLE dbo.Lookup;
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NOT NULL DROP TABLE dbo.SchemaTemplateTenants;
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}';";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private static void DropTenantSchemasInternal(IDbCommand cmd, IEnumerable<string> tenants)
    {
        foreach (var tenant in tenants)
        {
            // Drop all objects in the schema, then drop the schema itself. Order: FKs first, then
            // VIEWS (indexed views with SCHEMABINDING block table drops), then procs, then tables.
            cmd.CommandText = $@"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + 'ALTER TABLE [' + s.name + '].[' + t.name + '] DROP CONSTRAINT [' + fk.name + '];' + CHAR(10)
  FROM sys.foreign_keys fk
  INNER JOIN sys.tables t ON fk.parent_object_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
SELECT @sql = @sql + 'DROP VIEW [' + s.name + '].[' + v.name + '];' + CHAR(10)
  FROM sys.views v
  INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
SELECT @sql = @sql + 'DROP PROCEDURE [' + s.name + '].[' + o.name + '];' + CHAR(10)
  FROM sys.objects o
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND o.type = 'P';
SELECT @sql = @sql + 'DROP TABLE [' + s.name + '].[' + t.name + '];' + CHAR(10)
  FROM sys.tables t
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
IF @sql <> '' EXEC sp_executesql @sql;
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }
    }

    private void ExecuteOnMainDb(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
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
            $"SELECT COUNT(*) FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{schema}' AND t.name = '{tableName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected table [{schema}].[{tableName}] to exist after quench.");
    }

    private void AssertIdenticalColumnsAcrossSchemas(IReadOnlyList<string> schemas, string tableName)
    {
        // Pull column shape (name + data type ordinal) for each schema's table; assert all sets match.
        List<string> reference = null;
        foreach (var schema in schemas)
        {
            var cols = QueryRows(
                $"SELECT c.name + ':' + ty.name FROM sys.columns c INNER JOIN sys.tables t ON c.object_id = t.object_id INNER JOIN sys.schemas s ON t.schema_id = s.schema_id INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id WHERE s.name = '{schema}' AND t.name = '{tableName}' ORDER BY c.column_id");
            reference ??= cols;
            Assert.That(cols, Is.EquivalentTo(reference),
                $"Schema '{schema}' table '{tableName}' columns differ from reference set — per-iteration structure is not identical.");
        }
    }

    private void AssertMigrationTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND ScriptPath = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected a tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}').");
    }

    private int ScalarCount(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
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
