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

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Slice-8 end-to-end integration validator for the Schema Templates feature, deploying the
/// <c>TestProducts/SqlServer/TenantCRM</c>, a copy of the public-facing <c>Demos/SqlServer/TenantCRM</c> demo product,
/// against a live SQL Server. A copy, because CI does not run for a change under <c>Demos/</c>: re-copy the demo
/// when it changes, so the test keeps proving the feature delivers what the demo claims.
///
/// <para>Test walks the full lifecycle exactly the way the README walks the user through it:
/// (1) fresh quench against an empty <c>dbo.Tenants</c> deploys the Shared template only;
/// (2) <c>dbo.OnboardTenant</c> onboards three tenants; (3) re-quench iterates per tenant;
/// (4) per-tenant structure / FKs / migration tracking / procedures verified; (5) onboard a
/// fourth tenant; (6) selective quench with <c>Target.Schemas</c> deploys only the fourth
/// tenant; (7) original three tenants' state untouched.</para>
/// </summary>
[Category("SqlServer")]
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
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [Test]
    public void Full_Lifecycle_Fresh_Deploy_Onboard_Three_Then_Selective_Onboard_Fourth()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetDemoState();

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            // Re-point the demo's TenantCRMDb token at the CI-provisioned TestMain database
            // so the demo deploys without creating its own database. Initialize template's
            // discovery script resolves to "no matching db needs creation" when the token
            // and the current db are the same name.
            config["ScriptTokens:TenantCRMDb"] = _mainDb;
            ClearTargetFilters(config);

            try
            {
                // ----- Quench #1: empty dbo.Tenants → Shared deploys; TenantWorkspace iterates 0 schemas.
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                AssertTableExists("dbo", "Tenants");
                AssertTableExists("dbo", "Plans");
                AssertTableExists("dbo", "Countries");
                AssertTableExists("dbo", "GlobalAuditLog");
                AssertProcedureExists("dbo", "OnboardTenant");

                // Plans + Countries seed via DataDelivery.
                Assert.That(ScalarCount("SELECT COUNT(*) FROM dbo.Plans"), Is.EqualTo(3),
                    "Shared template's Plans seed must deliver 3 rows.");
                Assert.That(ScalarCount("SELECT COUNT(*) FROM dbo.Countries"), Is.EqualTo(8),
                    "Shared template's Countries seed must deliver 8 rows.");

                // No tenant iterations should have happened — dbo.Tenants is empty.
                foreach (var tenant in InitialTenants)
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // ----- Onboard three tenants via dbo.OnboardTenant.
                foreach (var (name, displayName, planId) in new[]
                         {
                             ("tenant_acme", "Acme Corporation", 2),
                             ("tenant_beta", "Beta Industries", 1),
                             ("tenant_globex", "Globex Holdings", 3)
                         })
                {
                    OnboardTenant(name, displayName, planId);
                }

                Assert.That(ScalarCount("SELECT COUNT(*) FROM dbo.Tenants WHERE Status = N'Active'"),
                    Is.EqualTo(3), "OnboardTenant should have inserted 3 active tenant rows.");
                foreach (var tenant in InitialTenants)
                {
                    Assert.That(ScalarCount($"SELECT COUNT(*) FROM sys.schemas WHERE name = '{tenant}'"),
                        Is.EqualTo(1), $"OnboardTenant should have created schema [{tenant}].");
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
                    AssertTableExists(tenant, "Customers");
                    AssertTableExists(tenant, "Contacts");
                    AssertTableExists(tenant, "Activities");
                    AssertTableExists(tenant, "ActivityTypes");
                    AssertProcedureExists(tenant, "AddCustomer");
                    AssertProcedureExists(tenant, "RecordActivity");
                    AssertFunctionExists(tenant, "GetCustomerLifetimeValue");
                    AssertViewExists(tenant, "ActiveCustomers");
                    AssertTriggerExists(tenant, "Customers_AuditLastModified");
                    AssertIndexedViewExists(tenant, "vw_ActiveCustomerCount");
                }
                AssertIdenticalColumnsAcrossSchemas(InitialTenants, "Customers");
                AssertIdenticalColumnsAcrossSchemas(InitialTenants, "Activities");

                // ----- Cross-schema FK: tenant.Customers.CountryCode → dbo.Countries.Code.
                foreach (var tenant in InitialTenants)
                {
                    var crossSchemaFk = ScalarCount($@"
SELECT COUNT(*) FROM sys.foreign_keys fk
  INNER JOIN sys.tables ft ON fk.parent_object_id = ft.object_id
  INNER JOIN sys.schemas fs ON ft.schema_id = fs.schema_id
  INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
  INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
WHERE fs.name = '{tenant}' AND ft.name = 'Customers' AND fk.name = 'FK_Customers_Countries'
  AND rs.name = 'dbo' AND rt.name = 'Countries'");
                    Assert.That(crossSchemaFk, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have FK_Customers_Countries pointing at dbo.Countries.");
                }

                // ----- Migration tracking: Migration_001_BackfillCustomerCountryCode per tenant.
                // ActivityTypes is seeded via DataDelivery (MergeType=Insert) — see assertion
                // immediately below — and is not tracked as a migration.
                foreach (var tenant in InitialTenants)
                {
                    AssertMigrationTracked(TenantWorkspaceTemplate, tenant,
                        "Before Scripts/Migration_001_BackfillCustomerCountryCode.sql");

                    // DataDelivery should have inserted the 4 default activity types per tenant.
                    Assert.That(ScalarCount($"SELECT COUNT(*) FROM [{tenant}].[ActivityTypes]"),
                        Is.EqualTo(4), $"Tenant '{tenant}' should have 4 DataDelivery-seeded activity types.");
                }

                // ----- Procedures wire to dbo.GlobalAuditLog via {{SchemaName}} token resolution.
                ExecuteOnMainDb(@"DECLARE @id INT; EXEC [tenant_acme].[AddCustomer]
                    @CustomerName = N'Wile E. Coyote', @Email = N'wile@acme.example', @CountryCode = 'US',
                    @CustomerID = @id OUTPUT;");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM dbo.GlobalAuditLog WHERE TenantName = N'tenant_acme' AND EventType = N'CustomerAdded'"),
                    Is.GreaterThanOrEqualTo(1),
                    "tenant_acme.AddCustomer must have written to the global audit log with TenantName='tenant_acme'.");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM [tenant_acme].[Customers] WHERE [CustomerName] = N'Wile E. Coyote'"),
                    Is.EqualTo(1), "Customer should be in tenant_acme.Customers.");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM [tenant_beta].[Customers]"),
                    Is.EqualTo(0), "tenant_beta must NOT have received the customer — schema isolation.");

                // ----- Quench #3: idempotent re-quench — migrations should skip.
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Still exactly 1 migration row per tenant (no duplicates).
                foreach (var tenant in InitialTenants)
                {
                    var migrationCount = ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}' AND template_name = '{TenantWorkspaceTemplate}' AND schema_name = '{tenant}'");
                    Assert.That(migrationCount, Is.EqualTo(1),
                        $"Tenant '{tenant}' must have exactly 1 tracked migration after re-quench (no duplicates).");
                }

                // Slice-3 audit B5: per-tenant indexed views must survive a second quench. Pre-fix,
                // SchemaSmith.IndexedViewQuench saw sibling tenants' vw_ActiveCustomerCount as
                // "removed from product" and dropped them; post-fix the existing-views lookup is
                // scoped to (@TemplateName, @SchemaName). This is the user-visible demo form of
                // the regression already covered by SchemaTemplateHappyPathTests.
                foreach (var tenant in InitialTenants)
                    AssertIndexedViewExists(tenant, "vw_ActiveCustomerCount");

                // ----- Onboard a fourth tenant; selective quench targets only that tenant.
                OnboardTenant(FourthTenant, "Fourth Co", 1);

                // Capture original-three pre-state so we can prove selective quench didn't touch them.
                var preActivityTypeCounts = InitialTenants
                    .ToDictionary(t => t, t => ScalarCount($"SELECT COUNT(*) FROM [{t}].[ActivityTypes]"));
                var preMigrationCounts = InitialTenants
                    .ToDictionary(t => t, t => ScalarCount(
                        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}' AND template_name = '{TenantWorkspaceTemplate}' AND schema_name = '{t}'"));

                config["Target:Schemas:0"] = FourthTenant;
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Fourth tenant must be fully deployed.
                AssertTableExists(FourthTenant, "Customers");
                AssertTableExists(FourthTenant, "ActivityTypes");
                AssertProcedureExists(FourthTenant, "AddCustomer");
                AssertIndexedViewExists(FourthTenant, "vw_ActiveCustomerCount");
                AssertMigrationTracked(TenantWorkspaceTemplate, FourthTenant,
                    "Before Scripts/Migration_001_BackfillCustomerCountryCode.sql");
                Assert.That(ScalarCount($"SELECT COUNT(*) FROM [{FourthTenant}].[ActivityTypes]"),
                    Is.EqualTo(4), "Fourth tenant should have 4 DataDelivery-seeded activity types.");

                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: {FourthTenant}] Successfully Quenched");

                // Original three tenants must be untouched by the selective quench.
                foreach (var tenant in InitialTenants)
                {
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                    Assert.That(ScalarCount($"SELECT COUNT(*) FROM [{tenant}].[ActivityTypes]"),
                        Is.EqualTo(preActivityTypeCounts[tenant]),
                        $"Tenant '{tenant}' activity-type row count must be unchanged by selective quench.");

                    Assert.That(ScalarCount(
                            $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}' AND template_name = '{TenantWorkspaceTemplate}' AND schema_name = '{tenant}'"),
                        Is.EqualTo(preMigrationCounts[tenant]),
                        $"Tenant '{tenant}' migration tracking must be unchanged by selective quench.");
                }

                // tenant_acme's customer row from earlier must still be there.
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM [tenant_acme].[Customers] WHERE [CustomerName] = N'Wile E. Coyote'"),
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

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        // Drop tenant schemas (FK / view / proc / table teardown in one block).
        var allTenants = InitialTenants.Concat(new[] { FourthTenant });
        DropTenantSchemasInternal(cmd, allTenants);

        // Drop the demo's dbo objects (in FK order).
        cmd.CommandText = @"
IF OBJECT_ID('dbo.OnboardTenant', 'P') IS NOT NULL DROP PROCEDURE dbo.OnboardTenant;
IF OBJECT_ID('dbo.Tenants', 'U') IS NOT NULL DROP TABLE dbo.Tenants;
IF OBJECT_ID('dbo.Plans', 'U') IS NOT NULL DROP TABLE dbo.Plans;
IF OBJECT_ID('dbo.Countries', 'U') IS NOT NULL DROP TABLE dbo.Countries;
IF OBJECT_ID('dbo.GlobalAuditLog', 'U') IS NOT NULL DROP TABLE dbo.GlobalAuditLog;
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '" + ProductName + @"';
IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '" + ProductName + @"';";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private static void DropTenantSchemasInternal(IDbCommand cmd, IEnumerable<string> tenants)
    {
        foreach (var tenant in tenants)
        {
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
SELECT @sql = @sql + 'DROP TRIGGER [' + s.name + '].[' + tr.name + '];' + CHAR(10)
  FROM sys.triggers tr
  INNER JOIN sys.tables t ON tr.parent_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
SELECT @sql = @sql + 'DROP PROCEDURE [' + s.name + '].[' + o.name + '];' + CHAR(10)
  FROM sys.objects o
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND o.type = 'P';
SELECT @sql = @sql + 'DROP FUNCTION [' + s.name + '].[' + o.name + '];' + CHAR(10)
  FROM sys.objects o
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND o.type IN ('FN', 'IF', 'TF');
SELECT @sql = @sql + 'DROP TABLE [' + s.name + '].[' + t.name + '];' + CHAR(10)
  FROM sys.tables t
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
IF @sql <> '' EXEC sp_executesql @sql;
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }
    }

    private void OnboardTenant(string name, string displayName, int planId)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
EXEC dbo.OnboardTenant
    @Name = N'{name}',
    @DisplayName = N'{displayName}',
    @PlanID = {planId};";
        cmd.ExecuteNonQuery();
        conn.Close();
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

    private void AssertProcedureExists(string schema, string procName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM sys.procedures p INNER JOIN sys.schemas s ON p.schema_id = s.schema_id WHERE s.name = '{schema}' AND p.name = '{procName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected procedure [{schema}].[{procName}] to exist after quench.");
    }

    private void AssertFunctionExists(string schema, string fnName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM sys.objects o INNER JOIN sys.schemas s ON o.schema_id = s.schema_id WHERE s.name = '{schema}' AND o.name = '{fnName}' AND o.type IN ('FN','IF','TF')");
        Assert.That(count, Is.EqualTo(1), $"Expected function [{schema}].[{fnName}] to exist after quench.");
    }

    private void AssertViewExists(string schema, string viewName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM sys.views v INNER JOIN sys.schemas s ON v.schema_id = s.schema_id WHERE s.name = '{schema}' AND v.name = '{viewName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected view [{schema}].[{viewName}] to exist after quench.");
    }

    private void AssertTriggerExists(string schema, string triggerName)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM sys.triggers tr INNER JOIN sys.tables t ON tr.parent_id = t.object_id INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{schema}' AND tr.name = '{triggerName}'");
        Assert.That(count, Is.EqualTo(1), $"Expected trigger [{schema}].[{triggerName}] to exist after quench.");
    }

    private void AssertIndexedViewExists(string schema, string viewName)
    {
        // Schema-bound indexed view check — IsIndexed = 1 confirms the unique clustered index
        // landed (without it, an indexed view degrades to a regular view and the iteration would
        // not have exercised SchemaSmith.IndexedViewQuench at all).
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM sys.views v INNER JOIN sys.schemas s ON v.schema_id = s.schema_id WHERE s.name = '{schema}' AND v.name = '{viewName}' AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1");
        Assert.That(count, Is.EqualTo(1),
            $"Expected schema-bound indexed view [{schema}].[{viewName}] (with unique clustered index) to exist after quench.");
    }

    private void AssertIdenticalColumnsAcrossSchemas(IReadOnlyList<string> schemas, string tableName)
    {
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
