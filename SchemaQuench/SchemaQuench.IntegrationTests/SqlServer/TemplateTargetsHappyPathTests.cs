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

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// TemplateTargets integration tests for SQL Server. Covers the existing-tenants happy path
/// (override REPLACES SchemaDiscovery, source-disclosure log surfaces override origin, downstream
/// tracking matches a discovery-driven run on the same tenant set), the provisioning +
/// skip-missing paths (<c>CreateIfMissing: true</c> provisions missing schemas via per-engine
/// idempotent DDL; <c>CreateIfMissing: false</c> default skips missing entries with an info log),
/// and the database-axis provisioning + skip-missing variants.
/// </summary>
[Category("SqlServer")]
public class TemplateTargetsHappyPathTests
{
    private const string ProductName = "SchemaTemplateProduct";
    private const string TenantBodyTemplate = "TenantBody";

    private static readonly string[] DefaultTenants = ["tenant_acme", "tenant_beta", "tenant_globex"];

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public TemplateTargetsHappyPathTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [Test]
    public void OverrideSchemasListReplacesDiscoveryScript_ExistingTenants()
    {
        // Two tenants exist on the target. Override Target.TemplateTargets.TenantBody.Schemas
        // to those two and quench. The override REPLACES SchemaDiscovery — even though the
        // dbo.SchemaTemplateTenants table is populated, the override list IS the universe.
        // To prove that, populate dbo.SchemaTemplateTenants with EXTRA tenants the override
        // omits and assert no per-tenant work landed for those extras.
        var overrideTenants = new[] { "tenant_acme", "tenant_globex" };
        var extraTenantIgnoredByOverride = "tenant_beta";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            // Configure the override after the dbo.SchemaTemplateTenants table is seeded with
            // ALL three tenants — the override must override the discovery result.
            config["Target:TemplateTargets:TenantBody:Schemas:0"] = overrideTenants[0];
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = overrideTenants[1];

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Only the two overridden tenants were quenched.
                foreach (var tenant in overrideTenants)
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: {extraTenantIgnoredByOverride}] Successfully Quenched");

                // Source disclosure still names the override origin for the schema axis, now as the
                // per-template rollup rather than a line per work unit (per-unit detail moved to the report).
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains("Template 'TenantBody'") &&
                    s.Contains("schema: TemplateTargets:TenantBody:Schemas")));

                // Both overridden tenants got their per-iteration migration tracking.
                foreach (var tenant in overrideTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant,
                        "MigrationScripts/Before/SeedTenantMarker.sql");

                // The omitted tenant's schema was NOT touched: no tracking row.
                AssertMigrationNotTracked(TenantBodyTemplate, extraTenantIgnoredByOverride,
                    "MigrationScripts/Before/SeedTenantMarker.sql");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void OverrideWithCreateIfMissing_ProvisionsMissingSchemasAndDeploys()
    {
        // CreateIfMissing: true — override lists one existing tenant + one schema NOT yet created
        // on the target. SchemaProvisioner emits idempotent CREATE SCHEMA for the missing one,
        // then per-iteration deployment runs against both. Both end up with template content
        // and a provisioning log line surfaces for the created schema.
        const string existingTenant = "tenant_acme";
        const string newTenant = "tenant_newly_created";
        var overrideSchemas = new[] { existingTenant, newTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            // Pre-create only the existing tenant. The new tenant's schema does NOT exist yet.
            ResetTrackingAndCreateTenantSchemas(new[] { existingTenant });
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:TenantBody:Schemas:0"] = overrideSchemas[0];
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = overrideSchemas[1];
            config["Target:TemplateTargets:TenantBody:CreateIfMissing"] = "true";

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Both tenants completed their iteration (the missing one was provisioned first).
                foreach (var tenant in overrideSchemas)
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // Provisioning log line surfaced for the new tenant.
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Creating schema [{newTenant}]") &&
                    s.Contains("CreateIfMissing: true")));

                // The new tenant's schema now exists on the target.
                Assert.That(SchemaExists(newTenant), Is.True,
                    "CreateIfMissing: true must provision missing override entries.");

                // Both tenants have migration tracking rows.
                foreach (var tenant in overrideSchemas)
                    AssertMigrationTracked(TenantBodyTemplate, tenant,
                        "MigrationScripts/Before/SeedTenantMarker.sql");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTenantSchemas(new[] { existingTenant, newTenant });
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void DatabaseOverrideWithCreateIfMissing_ProvisionsMissingDbAndDeploys()
    {
        // Databases override + CreateIfMissing: true → admin-DB connection to master,
        // CREATE DATABASE for the missing target, then quench inside it. We use the Shared
        // template (no schema-axis) and limit Target.Templates to it so TenantBody (which
        // assumes a tenant-table seeded in the original MainDB) doesn't try to run against the
        // transient DB.
        //
        // Kindling must run for this test: the freshly-provisioned DB has no SchemaSmith
        // helpers; the rest of the deployment pipeline depends on them (e.g.,
        // SchemaSmith.fn_SafeBracketWrap inside MissingTableAndColumnQuench). Invoke
        // Program.Main with no positional args so KindleTheForge runs end-to-end.
        var transientDb = MakeTransientDbName("ttdb_create");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(transientDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            // Scope to the Shared template — the Shared template's DatabaseIdentificationScript
            // returns MainDB only; the override REPLACES that with our transient DB.
            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:Databases:0"] = transientDb;
            config["Target:TemplateTargets:Shared:CreateIfMissing"] = "true";

            try
            {
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Provisioning log line surfaced for the new DB on the admin connection. The
                // line shape is the provisioner's per-engine quoted-form: "Creating database
                // [X] (CreateIfMissing: true)" on SQL Server.
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Creating database [{transientDb}]") &&
                    s.Contains("CreateIfMissing: true")));

                // The transient DB now exists and has the Shared template content deployed.
                Assert.That(DatabaseExists(transientDb), Is.True,
                    "CreateIfMissing: true must provision the missing DB.");
                Assert.That(TableExistsInDb(transientDb, "dbo", "Lookup"), Is.True,
                    "Shared template deployment must have run inside the newly-provisioned DB.");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(transientDb);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void DeployWithLegacyCompatEncoding_KindlesXmlHelpersAndAppliesSchema()
    {
        // B3: Target:CompatEncoding=legacy forces the XML model-ingest encoding on a modern-compat DB (the CI
        // backbone tier — exercises the XML kindle + XML-ingest apply path without needing the supported floor
        // lowered). Deploy the Shared template into a TRANSIENT DB (so the legacy re-kindle never touches the
        // JSON-kindled shared MainDb) and assert the XML twin helpers were kindled (GenerateTableXml, an
        // Xml-only twin) and the schema applied end-to-end (dbo.Lookup) through the XML ingest path.
        var transientDb = MakeTransientDbName("ttdb_legacy");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(transientDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:Databases:0"] = transientDb;
            config["Target:TemplateTargets:Shared:CreateIfMissing"] = "true";
            config["Target:CompatEncoding"] = "legacy";

            try
            {
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // The legacy encoding kindled the XML twin helpers, not the JSON generate proc — proving the
                // detected encoding was threaded to KindleTheForge.
                Assert.That(ObjectExistsInDb(transientDb, "SchemaSmith.GenerateTableXml", "P"), Is.True,
                    "Target:CompatEncoding=legacy must kindle the XML compare twin (encoding threaded to KindleTheForge).");
                Assert.That(ObjectExistsInDb(transientDb, "SchemaSmith.GenerateTableJSON", "P"), Is.False,
                    "The JSON generate proc must NOT be kindled on the legacy encoding.");

                // The schema applied end-to-end through the XML ingest apply path.
                Assert.That(TableExistsInDb(transientDb, "dbo", "Lookup"), Is.True,
                    "The Shared template must deploy dbo.Lookup via the XML ingest apply path.");
            }
            finally
            {
                config["Target:CompatEncoding"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(transientDb);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void LegacyVsModernCompatEncoding_ProduceIdenticalSchema()
    {
        // The kindle+apply equivalence gate: deploy the SAME product under the XML (legacy) and JSON (modern)
        // model-ingest encodings to two transient DBs and assert the materialized user schema is identical.
        // Proves the XML ingest apply path converges the same schema as the JSON path end-to-end through
        // DatabaseQuench (table + column + index + constraint create), not just that it runs.
        var legacyDb = MakeTransientDbName("ttdb_eqL");
        var modernDb = MakeTransientDbName("ttdb_eqM");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(legacyDb);
            DropTransientDb(modernDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:CreateIfMissing"] = "true";

            try
            {
                // Legacy (XML) deploy.
                config["Target:TemplateTargets:Shared:Databases:0"] = legacyDb;
                config["Target:CompatEncoding"] = "legacy";
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Modern (JSON) deploy of the same product.
                ClearCheckpointsForProduct();
                config["Target:TemplateTargets:Shared:Databases:0"] = modernDb;
                config["Target:CompatEncoding"] = "modern";
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                var legacySig = CaptureUserSchemaSignature(legacyDb);
                var modernSig = CaptureUserSchemaSignature(modernDb);
                Assert.That(legacySig, Is.Not.Empty, "Signature capture must find the deployed user tables.");
                Assert.That(legacySig, Is.EqualTo(modernSig),
                    "The legacy (XML) ingest apply path must converge a schema identical to the modern (JSON) path.");
            }
            finally
            {
                config["Target:CompatEncoding"] = null;
                config["Target:TemplateTargets:Shared:CreateIfMissing"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(legacyDb);
                DropTransientDb(modernDb);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void RealCompat100Database_AutoSelectsXmlAndDeploys_MatchingModern()
    {
        // F2 floor proof: a REAL compat-100 database (NOT the Target:CompatEncoding override) now clears
        // pre-flight (the floor is compat 100, F1) and the ingest encoding auto-selects XML from the detected
        // compat, deploying a schema identical to a modern (compat-default, JSON) deploy of the same product.
        // Exercises the whole floor chain end to end: detect compat 100 → pass pre-flight → auto-select XML →
        // kindle XML helpers → apply. This is the compat-100 integration coverage inside the existing SQL
        // Server context (no new CI leg / status-check name). Before F1 the compat-100 target aborted pre-flight.
        var compat100Db = MakeTransientDbName("ttdb_c100");
        var modernDb = MakeTransientDbName("ttdb_c100M");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(compat100Db);
            DropTransientDb(modernDb);
            CreateDatabaseAtCompatLevel(compat100Db, 100);   // real compat 100 — no CompatEncoding override
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:CreateIfMissing"] = "true";

            try
            {
                // Real compat-100 deploy — NO CompatEncoding override; the encoding auto-selects from detected compat.
                config["Target:TemplateTargets:Shared:Databases:0"] = compat100Db;
                config["Target:CompatEncoding"] = null;
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Modern (compat-default → JSON) deploy of the same product.
                ClearCheckpointsForProduct();
                config["Target:TemplateTargets:Shared:Databases:0"] = modernDb;
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                Assert.That(GetCompatLevel(compat100Db), Is.EqualTo(100),
                    "Target database must remain at compatibility level 100 (guards against a silent reset).");
                var c100Sig = CaptureUserSchemaSignature(compat100Db);
                var modernSig = CaptureUserSchemaSignature(modernDb);
                Assert.That(c100Sig, Is.Not.Empty, "Signature capture must find the deployed user tables.");
                Assert.That(c100Sig, Is.EqualTo(modernSig),
                    "A real compat-100 deploy (auto-selected XML ingest) must converge a schema identical to the modern (JSON) deploy.");
            }
            finally
            {
                config["Target:CompatEncoding"] = null;
                config["Target:TemplateTargets:Shared:CreateIfMissing"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(compat100Db);
                DropTransientDb(modernDb);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void DatabaseOverrideWithoutCreateIfMissing_SkipsMissingDbWithInfoLog()
    {
        // CreateIfMissing: false (default) → missing DBs are SKIPPED with an info log; no
        // admin-DB CREATE DATABASE issued; no work units run for that DB. Mix one existing DB
        // (MainDb) with one missing DB so the work-unit list isn't empty (which would trip
        // RequireAtLeastOneTarget: true — the default).
        var missingDb = MakeTransientDbName("ttdb_skip");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(missingDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:Databases:0"] = _mainDb;
            config["Target:TemplateTargets:Shared:Databases:1"] = missingDb;
            // CreateIfMissing intentionally absent — defaults to false.

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Skip log line names the missing DB + the CreateIfMissing-is-false reason.
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Database '{missingDb}'") &&
                    s.Contains("CreateIfMissing is false") &&
                    s.Contains("skipping all iterations for this server-database pair")));

                // The missing DB must NOT have been created (negative control).
                Assert.That(DatabaseExists(missingDb), Is.False,
                    "Skip-missing must NOT provision the database.");

                // The existing DB (MainDb) DID deploy its template content.
                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] Successfully Quenched");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(missingDb);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void OverrideWithoutCreateIfMissing_SkipsMissingSchemasWithInfoLog()
    {
        // CreateIfMissing: false (default) — override lists one existing tenant + one missing tenant.
        // The missing one is SKIPPED with an info log naming it; the existing one deploys normally.
        // The missing schema is NOT created (the negative-control: the engine must not silently
        // upgrade a skip-missing override into a create).
        const string existingTenant = "tenant_acme";
        const string missingTenant = "tenant_skipped";
        var overrideSchemas = new[] { existingTenant, missingTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(new[] { existingTenant });
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:TenantBody:Schemas:0"] = overrideSchemas[0];
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = overrideSchemas[1];
            // CreateIfMissing intentionally absent — defaults to false.

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Existing tenant completed.
                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: {existingTenant}] Successfully Quenched");

                // Missing tenant did NOT complete (skip-missing — the unit short-circuits).
                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: {missingTenant}] Successfully Quenched");

                // Skip log line names the missing schema + the CreateIfMissing-is-false reason.
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Schema '{missingTenant}'") &&
                    s.Contains("TemplateTargets CreateIfMissing is false") &&
                    s.Contains("skipping this iteration")));

                // Missing schema must NOT have been created (negative control).
                Assert.That(SchemaExists(missingTenant), Is.False,
                    "Skip-missing must NOT provision the schema.");

                // Migration tracking exists for the existing tenant, NOT for the skipped one.
                AssertMigrationTracked(TenantBodyTemplate, existingTenant,
                    "MigrationScripts/Before/SeedTenantMarker.sql");
                AssertMigrationNotTracked(TenantBodyTemplate, missingTenant,
                    "MigrationScripts/Before/SeedTenantMarker.sql");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTenantSchemas(new[] { existingTenant, missingTenant });
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

    // DB-axis tests provisioning new DBs need full kindling — the freshly-provisioned DB has
    // no SchemaSmith helpers, and downstream deployment depends on them.
    private static void RunSchemaQuenchWithKindling() => Program.Main(System.Array.Empty<string>());

    private static void ClearTargetFilters(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTargetFilters(config);

    private static void ClearTemplateTargets(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTemplateTargets(config);

    private static void ClearCheckpointsForProduct()
    {
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

        cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}';
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NOT NULL DELETE FROM dbo.SchemaTemplateTenants;
IF OBJECT_ID('dbo.Lookup', 'U') IS NOT NULL DELETE FROM dbo.Lookup;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, tenants);

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = @"
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NULL
    CREATE TABLE dbo.SchemaTemplateTenants ([Name] NVARCHAR(128) NOT NULL CONSTRAINT PK_SchemaTemplateTenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

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

    private void AssertMigrationTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND ScriptPath = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected a tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}').");
    }

    private void AssertMigrationNotTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND ScriptPath = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(0),
            $"Expected NO tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}') — the targeted run must not have touched this scope.");
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

    private bool SchemaExists(string schemaName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.schemas WHERE name = @name";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = schemaName;
        cmd.Parameters.Add(p);
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }

    // ----- Slice 4 helpers (DB-axis) ----------------------------------------------------------

    private static string MakeTransientDbName(string prefix)
    {
        // Keep the name short + unique. Tests use these as the override target for DB-axis
        // provisioning; teardown drops them so CI doesn't leak DBs across runs.
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"{prefix}_{unique}";
    }

    private bool DatabaseExists(string databaseName)
    {
        // Connect to master (the constructor's connection string already targets master).
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = databaseName;
        cmd.Parameters.Add(p);
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }

    private bool TableExistsInDb(string databaseName, string schemaName, string tableName)
    {
        if (!DatabaseExists(databaseName)) return false;
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(databaseName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                          $"WHERE s.name = '{schemaName}' AND t.name = '{tableName}'";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }

    // A deterministic signature of the deployed USER schema (columns + indexes), excluding SchemaSmith's own
    // helper objects. Used to assert the XML and JSON ingest apply paths converge an identical schema.
    private string CaptureUserSchemaSignature(string databaseName)
    {
        if (!DatabaseExists(databaseName)) return "";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(databaseName);
        using var cmd = conn.CreateCommand();
        // COLLATE DATABASE_DEFAULT on every string operand: INFORMATION_SCHEMA/sys expose catalog-collation
        // sysname columns that otherwise conflict with DB-collation literals in the per-row concatenation.
        // The rows are aggregated in C# (sort + join) rather than STRING_AGG so the capture also works
        // against a compat-100 database (STRING_AGG ... WITHIN GROUP is compat-130-gated).
        cmd.CommandText = @"
SELECT 'COL|' + TABLE_SCHEMA COLLATE DATABASE_DEFAULT + '.' + TABLE_NAME COLLATE DATABASE_DEFAULT + '|' +
       COLUMN_NAME COLLATE DATABASE_DEFAULT + '|' + DATA_TYPE COLLATE DATABASE_DEFAULT + '|' +
       ISNULL(CONVERT(VARCHAR(20), CHARACTER_MAXIMUM_LENGTH), '') + '|' + IS_NULLABLE COLLATE DATABASE_DEFAULT AS sig
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA <> 'SchemaSmith' AND TABLE_NAME NOT LIKE 'SchemaSmith[_]%'
UNION ALL
SELECT 'IDX|' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT + '|' +
       i.name COLLATE DATABASE_DEFAULT + '|' + CONVERT(CHAR(1), i.is_unique) + '|' +
       CONVERT(CHAR(1), i.is_primary_key) + '|' + i.type_desc COLLATE DATABASE_DEFAULT AS sig
  FROM sys.indexes i
  JOIN sys.tables t ON i.object_id = t.object_id
  JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE i.index_id > 0 AND s.name <> 'SchemaSmith'";
        var sigs = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                sigs.Add(reader.GetString(0));
        conn.Close();
        sigs.Sort(StringComparer.Ordinal);
        return string.Join("\n", sigs);
    }

    private bool ObjectExistsInDb(string databaseName, string objectName, string type)
    {
        if (!DatabaseExists(databaseName)) return false;
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(databaseName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('{objectName}', '{type}') IS NULL THEN 0 ELSE 1 END";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) == 1;
    }

    private void CreateDatabaseAtCompatLevel(string databaseName, int compatLevel)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = {compatLevel};";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private int GetCompatLevel(string databaseName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT compatibility_level FROM sys.databases WHERE name = '{databaseName.Replace("'", "''")}'";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result);
    }

    private void DropTransientDb(string databaseName)
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $@"
IF DB_ID('{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch
        {
            // Best-effort cleanup — a missing or already-dropped DB is fine.
        }
    }
}
