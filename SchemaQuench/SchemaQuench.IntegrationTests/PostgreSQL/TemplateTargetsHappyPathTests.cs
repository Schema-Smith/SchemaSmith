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

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// TemplateTargets integration tests for PostgreSQL — mirrors the SQL Server fixture. Covers the
/// existing-tenants enumeration-override happy path, the provisioning (<c>CreateIfMissing: true</c>)
/// and skip-missing (<c>CreateIfMissing: false</c>) paths on the schema axis, and the database-axis
/// provisioning + skip-missing variants.
/// </summary>
[Category("PostgreSQL")]
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
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [Test]
    public void OverrideSchemasListReplacesDiscoveryScript_ExistingTenants()
    {
        // Two tenants exist on the target. Override Target.TemplateTargets.TenantBody.Schemas to
        // those two and quench. Even with the public.schematemplate_tenants table populated with
        // all three tenants, only the overridden two should land per-iteration work.
        var overrideTenants = new[] { "tenant_acme", "tenant_globex" };
        var extraTenantIgnoredByOverride = "tenant_beta";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:TenantBody:Schemas:0"] = overrideTenants[0];
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = overrideTenants[1];

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in overrideTenants)
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: {extraTenantIgnoredByOverride}] Successfully Quenched");

                // Source disclosure still names the override origin, now as the per-template rollup
                // rather than a line per work unit (the per-unit detail moved to the report).
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains("Template 'TenantBody'") &&
                    s.Contains("schema: TemplateTargets:TenantBody:Schemas")));

                foreach (var tenant in overrideTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant,
                        "Before Scripts/SeedTenantMarker.sql");

                AssertMigrationNotTracked(TenantBodyTemplate, extraTenantIgnoredByOverride,
                    "Before Scripts/SeedTenantMarker.sql");
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
        // CreateIfMissing: true — override lists one existing tenant + one schema NOT yet created.
        // SchemaProvisioner emits CREATE SCHEMA IF NOT EXISTS for the missing one, then per-iteration
        // deployment runs against both.
        const string existingTenant = "tenant_acme";
        const string newTenant = "tenant_newly_created";
        var overrideSchemas = new[] { existingTenant, newTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(new[] { existingTenant });
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:TenantBody:Schemas:0"] = overrideSchemas[0];
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = overrideSchemas[1];
            config["Target:TemplateTargets:TenantBody:CreateIfMissing"] = "true";

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in overrideSchemas)
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Creating schema \"{newTenant}\"") &&
                    s.Contains("CreateIfMissing: true")));

                Assert.That(SchemaExists(newTenant), Is.True,
                    "CreateIfMissing: true must provision missing override entries.");

                foreach (var tenant in overrideSchemas)
                    AssertMigrationTracked(TenantBodyTemplate, tenant,
                        "Before Scripts/SeedTenantMarker.sql");
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
        // Databases override + CreateIfMissing: true → admin-DB connection to postgres,
        // CREATE DATABASE for the missing target (explicit existence check first, since PG has
        // no IF NOT EXISTS variant), then quench inside it. Kindling must run for this test: the
        // freshly-provisioned DB has no SchemaSmith helpers, and downstream deployment depends
        // on them.
        var transientDb = MakeTransientDbName("ttdb_create");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(transientDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:Databases:0"] = transientDb;
            config["Target:TemplateTargets:Shared:CreateIfMissing"] = "true";

            try
            {
                RunSchemaQuenchWithKindling();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // PG provisioner log line uses the engine's quoted-form: "Creating database
                // "X" (CreateIfMissing: true)".
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Creating database \"{transientDb}\"") &&
                    s.Contains("CreateIfMissing: true")));

                Assert.That(DatabaseExists(transientDb), Is.True,
                    "CreateIfMissing: true must provision the missing DB.");
                Assert.That(TableExistsInDb(transientDb, "public", "lookup"), Is.True,
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
    public void DatabaseOverrideWithoutCreateIfMissing_SkipsMissingDbWithInfoLog()
    {
        // Missing DB + CreateIfMissing: false → SKIP with info log; no CREATE. Mix one existing
        // DB (MainDb) + one missing DB so the work-unit list isn't empty (which would trip
        // RequireAtLeastOneTarget: true — the template default).
        var missingDb = MakeTransientDbName("ttdb_skip");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(missingDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = "Shared";
            config["Target:TemplateTargets:Shared:Databases:0"] = _mainDb;
            config["Target:TemplateTargets:Shared:Databases:1"] = missingDb;
            // CreateIfMissing absent — defaults false.

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Database '{missingDb}'") &&
                    s.Contains("CreateIfMissing is false") &&
                    s.Contains("skipping all iterations for this server-database pair")));

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
        // CreateIfMissing: false (default) — missing override entries are SKIPPED with an info log.
        // The schema is NOT created (negative control).
        const string existingTenant = "tenant_acme";
        const string missingTenant = "tenant_skipped";
        var overrideSchemas = new[] { existingTenant, missingTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(new[] { existingTenant });
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:TenantBody:Schemas:0"] = overrideSchemas[0];
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = overrideSchemas[1];

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: {existingTenant}] Successfully Quenched");

                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: {missingTenant}] Successfully Quenched");

                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Schema '{missingTenant}'") &&
                    s.Contains("TemplateTargets CreateIfMissing is false") &&
                    s.Contains("skipping this iteration")));

                Assert.That(SchemaExists(missingTenant), Is.False,
                    "Skip-missing must NOT provision the schema.");

                AssertMigrationTracked(TenantBodyTemplate, existingTenant,
                    "Before Scripts/SeedTenantMarker.sql");
                AssertMigrationNotTracked(TenantBodyTemplate, missingTenant,
                    "Before Scripts/SeedTenantMarker.sql");
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

    private void AssertMigrationTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND \"ScriptPath\" = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected a tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}').");
    }

    private void AssertMigrationNotTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND \"ScriptPath\" = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(0),
            $"Expected NO tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}').");
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

    private bool SchemaExists(string schemaName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @name";
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
        // Lowercase per PG convention; otherwise PG folds-or-quotes the name and the test diverges
        // from the override-supplied identifier. Tests use these as the override target for DB-axis
        // provisioning; teardown drops them so CI doesn't leak DBs across runs.
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"{prefix}_{unique}".ToLowerInvariant();
    }

    private bool DatabaseExists(string databaseName)
    {
        // Connect to the postgres admin DB. The constructor's _connectionString already targets it.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(databaseName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables " +
                          $"WHERE table_schema = '{schemaName}' AND table_name = '{tableName}'";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }

    private void DropTransientDb(string databaseName)
    {
        try
        {
            // pg_terminate_backend below is what frees the database; PG refuses DROP DATABASE
            // while any session still holds it.
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $@"
SELECT pg_terminate_backend(pid)
  FROM pg_stat_activity
  WHERE datname = '{databaseName}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\";";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch
        {
            // Best-effort cleanup — a missing or already-dropped DB is fine.
        }
    }
}
