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
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Slice-5 PostgreSQL correctness gate (design §9.7). Mirrors the SQL Server fixture.
/// </summary>
[Category("PostgreSQL")]
public class PruneScopeRespectsRunScopeTests
{
    private const string ProductName = "SchemaTemplateProduct";
    private const string TenantBodyTemplate = "TenantBody";

    private static readonly string[] DefaultTenants = ["tenant_acme", "tenant_beta", "tenant_globex"];

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public PruneScopeRespectsRunScopeTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [Test]
    public void Selective_Run_With_Prune_Enabled_Only_Prunes_Within_Scope()
    {
        var migrationFile = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", ProductName),
            "Templates", "TenantBody", "Before Scripts", "Migration_002.sql");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);

            try
            {
                File.WriteAllText(migrationFile, @"-- Disposable per-tenant migration for prune-scope test. Before-slot scripts run
-- before PK creation, so we use NOT EXISTS rather than ON CONFLICT.
INSERT INTO ""{{SchemaName}}"".customers (customer_id, marker)
  SELECT 2002, '{{SchemaName}}_m002'
  WHERE NOT EXISTS (SELECT 1 FROM ""{{SchemaName}}"".customers WHERE customer_id = 2002);
");

                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                foreach (var tenant in DefaultTenants)
                    AssertMigrationTracked(TenantBodyTemplate, tenant, "Before Scripts/Migration_002.sql");

                File.Delete(migrationFile);

                config["Target:Schemas:0"] = "tenant_acme";
                config["PruneObsoleteMigrationTracking"] = "true";
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                AssertMigrationNotTracked(TenantBodyTemplate, "tenant_acme",
                    "Before Scripts/Migration_002.sql");
                AssertMigrationTracked(TenantBodyTemplate, "tenant_beta",
                    "Before Scripts/Migration_002.sql");
                AssertMigrationTracked(TenantBodyTemplate, "tenant_globex",
                    "Before Scripts/Migration_002.sql");
            }
            finally
            {
                if (File.Exists(migrationFile)) File.Delete(migrationFile);
                ClearTargetFilters(config);
                config["PruneObsoleteMigrationTracking"] = null;
                DropTenantSchemas(DefaultTenants);
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

    /// <summary>
    /// Enumerates any live <c>Target:*</c> array slots and nulls them so the in-memory config
    /// shared across tests starts from a clean state. `.ToList()` snapshots the children first
    /// — enumerating live while mutating keys is asking for grief.
    /// </summary>
    private static void ClearTargetFilters(IConfigurationRoot config)
    {
        foreach (var dim in new[] { "Templates", "Databases", "Schemas" })
            foreach (var child in config.GetSection($"Target:{dim}").GetChildren().ToList())
                config[$"Target:{dim}:{child.Key}"] = null;
    }

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
}
