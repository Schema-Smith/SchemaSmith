// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Slice-4 failure isolation integration tests (design §5.5 and §10.4) — PostgreSQL mirror.
///
/// <para><b>ContinueOnSchemaFailure tests</b> use SchemaTemplateFailureProduct — a 5-tenant
/// fixture where tenant_boom triggers a deliberate RAISE EXCEPTION in Boom.sql.
/// AllowParallel is false so unit execution is deterministic.</para>
///
/// <para><b>ContinueOnDatabaseFailure tests</b> use SchemaTemplateDbFailureProduct — a fixture
/// whose SchemaIdentificationScript returns 'public' (a reserved PG name), causing SchemaDiscovery
/// to throw (a DB-level failure).</para>
///
/// <para>Abort-mode variants mutate the shared fixture's Template.json for the duration of the
/// test and restore it in finally — no separate *AbortProduct fixture directories needed.</para>
/// </summary>
[Category("PostgreSQL")]
public class FailureIsolationTests
{
    private const string FailureProductName = "SchemaTemplateFailureProduct";
    private const string DbFailureProductName = "SchemaTemplateDbFailureProduct";

    private static readonly string[] FiveTenants = ["tenant_a", "tenant_b", "tenant_boom", "tenant_c", "tenant_d"];

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public FailureIsolationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    /// <summary>
    /// ContinueOnSchemaFailure: true (default). 5 tenants, tenant_boom fails via RAISE EXCEPTION.
    /// The remaining 4 tenants complete. Exit code is non-zero. Failure summary names tenant_boom.
    /// </summary>
    [Test]
    public void ContinueOnSchemaFailure_True_OneFailure_OthersComplete_ExitNonZero()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", FailureProductName);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                _progressLog.Received().Error(Arg.Is<string>(msg => msg.Contains("tenant_boom")));

                foreach (var tenant in new[] { "tenant_a", "tenant_b", "tenant_c", "tenant_d" })
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                _progressLog.DidNotReceive().Info($"[{_server}].[{_mainDb}] [Schema: tenant_boom] Successfully Quenched");
            }
            finally
            {
                DropTenantSchemas(FiveTenants, FailureProductName);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// ContinueOnSchemaFailure: false. After the failure, dispatcher stops dispatching new
    /// units. Subsequent templates (PostFailure) do not run. Exit invoked.
    /// </summary>
    [Test]
    public void ContinueOnSchemaFailure_False_FailureAbortsTemplate_SubsequentTemplatesDoNotRun()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", FailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", FailureProductName);

            var modifiedJson = originalJson.Replace(
                "\"AllowParallel\": false,",
                "\"AllowParallel\": false,\n    \"ContinueOnSchemaFailure\": false,");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                _progressLog.DidNotReceive().Info(Arg.Is<string>(msg =>
                    msg.Contains("Quenching Template: PostFailure")));
            }
            finally
            {
                File.WriteAllText(templateJsonPath, originalJson);
                DropTenantSchemas(FiveTenants, FailureProductName);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Schema-template discovery failure (reserved-name guard rejects 'public') with
    /// ContinueOnSchemaFailure: false. Under the per-template scope contract, a schema
    /// template's failures — including discovery — are governed by ContinueOnSchemaFailure.
    /// Run must abort and the next template (PostFailure) must NOT run.
    /// </summary>
    [Test]
    public void Schema_Template_ContinueOnSchemaFailure_False_Discovery_Failure_Aborts()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName);

            var modifiedJson = originalJson.Replace(
                "\"ScriptFolders\": []",
                "\"ContinueOnSchemaFailure\": false,\n    \"ScriptFolders\": []");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                _progressLog.DidNotReceive().Info(Arg.Is<string>(msg =>
                    msg.Contains("Quenching Template: PostFailure")));
            }
            finally
            {
                File.WriteAllText(templateJsonPath, originalJson);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Per-template scope contract: ContinueOnDatabaseFailure has NO EFFECT on schema templates.
    /// A schema-kind failure (tenant_boom's Boom.sql RAISE EXCEPTION) with
    /// ContinueOnDatabaseFailure: false and ContinueOnSchemaFailure: true (default) MUST
    /// continue — schema templates honor only the schema policy, not the DB one. The next
    /// template (PostFailure) runs. Exit code non-zero.
    /// </summary>
    [Test]
    public void Schema_Template_ContinueOnDatabaseFailure_Has_No_Effect_When_ContinueOnSchemaFailure_True()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", FailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", FailureProductName);

            // Add ContinueOnDatabaseFailure:false while keeping ContinueOnSchemaFailure at its
            // default (true). The failure is schema-kind so the schema policy governs — should NOT abort.
            var modifiedJson = originalJson.Replace(
                "\"AllowParallel\": false,",
                "\"AllowParallel\": false,\n    \"ContinueOnDatabaseFailure\": false,");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                // Non-zero exit (schema-level failure recorded).
                _environment.Received().Exit(2);

                // PostFailure template DID run — schema continue policy allowed continuation.
                _progressLog.Received(1).Info(Arg.Is<string>(msg =>
                    msg.Contains("Quenching Template: PostFailure")));
            }
            finally
            {
                File.WriteAllText(templateJsonPath, originalJson);
                DropTenantSchemas(FiveTenants, FailureProductName);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// ContinueOnSchemaFailure: true (default). Schema template's SchemaIdentificationScript
    /// returns 'public' (reserved), causing SchemaDiscovery to throw — a schema-scope failure
    /// (per the new per-template-scope contract). The run logs the error but continues to the
    /// PostFailure template. Exit code is non-zero.
    /// </summary>
    [Test]
    public void Schema_Template_Discovery_Failure_Continues_Under_Default_ContinueOnSchemaFailure()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                _progressLog.Received().Error(Arg.Is<string>(msg =>
                    msg.Contains("Schema discovery FAILED") || msg.Contains("TenantBody")));

                _progressLog.Received(1).Info(Arg.Is<string>(msg =>
                    msg.Contains("Quenching Template: PostFailure")));
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Per-template scope contract: ContinueOnDatabaseFailure has NO EFFECT on schema templates,
    /// even for discovery failures. Schema-template discovery failure with
    /// ContinueOnDatabaseFailure: false and ContinueOnSchemaFailure: true (default) MUST
    /// continue. Regression test for the prior layered-bits behavior that incorrectly let
    /// ContinueOnDatabaseFailure abort schema-template discovery failures.
    /// </summary>
    [Test]
    public void Schema_Template_Discovery_Failure_Ignores_ContinueOnDatabaseFailure()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName);

            // ContinueOnDatabaseFailure: false would have aborted under the prior layered
            // model. Under the per-template-scope contract, it's ignored on schema templates.
            var modifiedJson = originalJson.Replace(
                "\"ScriptFolders\": []",
                "\"ContinueOnDatabaseFailure\": false,\n    \"ScriptFolders\": []");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                // PostFailure template DID run — ContinueOnDatabaseFailure had no effect.
                _progressLog.Received(1).Info(Arg.Is<string>(msg =>
                    msg.Contains("Quenching Template: PostFailure")));
            }
            finally
            {
                File.WriteAllText(templateJsonPath, originalJson);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Schema-template discovery failure with ContinueOnSchemaFailure: false MUST abort the run.
    /// Replaces the prior layered "ContinueOnDatabaseFailure: false aborts discovery failure"
    /// test — under the new per-template-scope contract, ContinueOnSchemaFailure is the relevant
    /// policy because the failure is inside a schema template's processing.
    /// </summary>
    [Test]
    public void Schema_Template_Discovery_Failure_Aborts_Under_ContinueOnSchemaFailure_False()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", DbFailureProductName);

            var modifiedJson = originalJson.Replace(
                "\"ScriptFolders\": []",
                "\"ContinueOnSchemaFailure\": false,\n    \"ScriptFolders\": []");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                _environment.Received().Exit(2);

                _progressLog.DidNotReceive().Info(Arg.Is<string>(msg =>
                    msg.Contains("Quenching Template: PostFailure")));
            }
            finally
            {
                File.WriteAllText(templateJsonPath, originalJson);
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

    private void ResetTrackingAndCreateTenantSchemas(IEnumerable<string> tenants, string productName)
    {
        ClearCheckpointsForProduct(productName);
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = @$"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{productName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" = '{productName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'tenants') THEN
        DELETE FROM public.tenants;
    END IF;
END;
$$;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, FiveTenants);

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{tenant}\";";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS public.tenants (name VARCHAR(128) NOT NULL CONSTRAINT pk_tenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO public.tenants (name) VALUES ('{tenant}');";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    private void DropTenantSchemas(IEnumerable<string> tenants, string productName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        DropTenantSchemasInternal(cmd, tenants);

        cmd.CommandText = $@"
DROP TABLE IF EXISTS public.tenants CASCADE;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts') THEN
        DELETE FROM ""SchemaSmith"".""CompletedMigrationScripts"" WHERE ""ProductName"" = '{productName}';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership') THEN
        DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""ProductName"" = '{productName}';
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

}
