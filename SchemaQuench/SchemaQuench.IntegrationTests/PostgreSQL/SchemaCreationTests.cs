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
/// Slice-4 CreateSchemaIfMissing integration tests (design §10.4) — PostgreSQL mirror.
///
/// <para>Uses SchemaTemplateProduct with test-setup variants rather than a dedicated fixture.
/// For "missing schema" tests we insert a tenant row without pre-creating the schema, so the
/// engine sees a schema name returned by discovery that does not exist in pg_namespace. That
/// exercises the CreateSchemaIfMissing path without altering the fixture at all.</para>
/// </summary>
[Category("PostgreSQL")]
public class SchemaCreationTests
{
    private const string ProductName = "SchemaTemplateProduct";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public SchemaCreationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    /// <summary>
    /// CreateSchemaIfMissing: false (default) + discovery returns a schema that does not exist
    /// in pg_namespace. The engine must fail that work unit with a clear error referencing the
    /// three onboarding paths. Other tenants whose schemas DO exist must complete.
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_DefaultFalse_MissingSchema_FailsWithClearError_OthersComplete()
    {
        const string missingTenant = "tenant_ghost";
        var existingTenants = new[] { "tenant_acme", "tenant_beta" };
        var allTenants = new List<string>(existingTenants) { missingTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            ResetTrackingAndCreateTenantSchemas(existingTenants);
            // Insert ghost tenant row only — no schema creation.
            ExecuteOnMainDb($"INSERT INTO public.schematemplate_tenants (name) VALUES ('{missingTenant}');");

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                _progressLog.Received().Error(Arg.Is<string>(msg =>
                    msg.Contains($"Schema '{missingTenant}'") &&
                    msg.Contains($"'{_mainDb}'") &&
                    msg.Contains("CreateSchemaIfMissing") &&
                    msg.Contains("Pre-create") &&
                    msg.Contains("Shared-template helper")));

                foreach (var tenant in existingTenants)
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                _progressLog.DidNotReceive().Info($"[{_server}].[{_mainDb}] [Schema: {missingTenant}] Successfully Quenched");

                Assert.That(SchemaExists(missingTenant), Is.False,
                    "The ghost schema must not be created when CreateSchemaIfMissing is false.");
            }
            finally
            {
                DropTenantSchemas(allTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// CreateSchemaIfMissing: true + discovery returns a schema that does not exist.
    /// The engine must emit CREATE SCHEMA "...", proceed, and the schema must exist after quench.
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_True_MissingSchema_CreatesSchemaAndProceeds()
    {
        const string autoTenant = "tenant_auto";
        var existingTenants = new[] { "tenant_acme" };
        var allTenants = new List<string>(existingTenants) { autoTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            ResetTrackingAndCreateTenantSchemas(existingTenants);
            ExecuteOnMainDb($"INSERT INTO public.schematemplate_createschema_tenants (name) VALUES ('{autoTenant}');");

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", "SchemaTemplateCreateSchemaProduct");

            try
            {
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.Received().Info(Arg.Is<string>(msg =>
                    msg.Contains($"Creating schema \"{autoTenant}\"") && msg.Contains("(CreateIfMissing: true)")));

                _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {autoTenant}] Successfully Quenched");

                Assert.That(SchemaExists(autoTenant), Is.True,
                    "Schema must be created when CreateSchemaIfMissing=true and schema was missing.");
            }
            finally
            {
                DropTenantSchemas(allTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Schema already exists + CreateSchemaIfMissing: true → engine skips creation and proceeds.
    /// CREATE SCHEMA must NOT be logged.
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_True_SchemaAlreadyExists_SkipsCreation()
    {
        var existingTenants = new[] { "tenant_acme", "tenant_beta" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(existingTenants);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", "SchemaTemplateCreateSchemaProduct");

            try
            {
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                _progressLog.DidNotReceive().Info(Arg.Is<string>(msg =>
                    msg.Contains("Creating schema") && msg.Contains("(CreateIfMissing: true)")));

                foreach (var tenant in existingTenants)
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");
            }
            finally
            {
                DropTenantSchemas(existingTenants);
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

    private void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private static void ClearCheckpointsForProduct(string productName)
    {
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(productName);
    }

    private void ResetTrackingAndCreateTenantSchemas(IEnumerable<string> tenants)
    {
        ClearCheckpointsForProduct(ProductName);
        ClearCheckpointsForProduct("SchemaTemplateCreateSchemaProduct");
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = @$"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'schematemplate_tenants') THEN
        DELETE FROM public.schematemplate_tenants;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'schematemplate_createschema_tenants') THEN
        DELETE FROM public.schematemplate_createschema_tenants;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'lookup') THEN
        DELETE FROM public.lookup;
    END IF;
END;
$$;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, new[] { "tenant_acme", "tenant_beta", "tenant_globex", "tenant_ghost", "tenant_auto" });

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{tenant}\";";
            cmd.ExecuteNonQuery();
        }

        // Seed BOTH renamed tenant tables — tests in this fixture exercise both
        // SchemaTemplateProduct (reads public.schematemplate_tenants) and
        // SchemaTemplateCreateSchemaProduct (reads public.schematemplate_createschema_tenants).
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS public.schematemplate_tenants (name VARCHAR(128) NOT NULL CONSTRAINT pk_schematemplate_tenants PRIMARY KEY);
CREATE TABLE IF NOT EXISTS public.schematemplate_createschema_tenants (name VARCHAR(128) NOT NULL CONSTRAINT pk_schematemplate_createschema_tenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO public.schematemplate_tenants (name) VALUES ('{tenant}');";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"INSERT INTO public.schematemplate_createschema_tenants (name) VALUES ('{tenant}');";
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
DROP TABLE IF EXISTS public.schematemplate_createschema_tenants CASCADE;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
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

    private bool SchemaExists(string schemaName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pg_namespace WHERE nspname = @name";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = schemaName;
        cmd.Parameters.Add(p);
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }
}
