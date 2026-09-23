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
using System.Diagnostics;
using System.Linq;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Post-slice-8 cleanup (Commit C) — 50-tenant perf smoke for schema templates on PostgreSQL.
/// This is an anchor for the "works at scale" claim, NOT a strict benchmark. It synthesizes 50
/// tenant schemas in a single discovery script return, runs the full schema-template fan-out
/// against the live PG container, and asserts the full quench completes under a generous wall
/// clock and that tracking advances for every tenant.
///
/// <para>Tagged <c>[Explicit]</c> so it does NOT run by default — every CI run otherwise would
/// pay 50 tenant iterations of cost. Run manually before a release or when investigating perf
/// regressions: <c>dotnet test --filter "Schema_Template_Fifty_Tenant_Perf_Smoke"</c>.</para>
/// </summary>
[Category("PostgreSQL")]
public class SchemaTemplatePerfSmokeTests
{
    private const string ProductName = "SchemaTemplateProduct";
    private const string TenantBodyTemplate = "TenantBody";
    private const int TenantCount = 50;
    private const int WallClockBudgetSeconds = 120;

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public SchemaTemplatePerfSmokeTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [Test]
    [Explicit("Perf smoke — opt-in. Runs a 50-tenant fan-out as a scale anchor; do not include in default CI.")]
    public void Schema_Template_Fifty_Tenant_Perf_Smoke()
    {
        var tenants = Enumerable.Range(0, TenantCount)
            .Select(i => $"perfsmoke_{i:D2}")
            .ToArray();

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(tenants);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("PostgreSQL", ProductName);

            try
            {
                var sw = Stopwatch.StartNew();
                RunSchemaQuench();
                sw.Stop();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // Wall-clock budget. The number is generous (the real target is "doesn't drag
                // for minutes"). If this is the only thing failing, investigate whether a perf
                // regression slipped in vs. raising the ceiling.
                Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(WallClockBudgetSeconds),
                    $"50-tenant fan-out should complete under {WallClockBudgetSeconds}s. Actual: {sw.Elapsed.TotalSeconds:F1}s.");

                // Every tenant advanced tracking — the discovery returned 50, the engine
                // dispatched a work unit for each, every iteration tracked SeedTenantMarker.
                var trackedTenantCount = ScalarCount(
                    $"SELECT COUNT(DISTINCT schema_name) FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{ProductName}' AND template_name = '{TenantBodyTemplate}' AND \"ScriptPath\" = 'Before Scripts/SeedTenantMarker.sql'");
                Assert.That(trackedTenantCount, Is.EqualTo(TenantCount),
                    $"Expected exactly {TenantCount} distinct tenants tracked for SeedTenantMarker — got {trackedTenantCount}.");

                // Sanity-check per-tenant work landed: spot-check the marker row on three tenants
                // (first, middle, last). Full row-by-row verification would dwarf the test value
                // for what this is — a perf anchor, not an exhaustive correctness probe.
                foreach (var probe in new[] { tenants[0], tenants[TenantCount / 2], tenants[TenantCount - 1] })
                {
                    var count = ScalarCount(
                        $"SELECT COUNT(*) FROM \"{probe}\".customers WHERE marker = '{probe}_marker'");
                    Assert.That(count, Is.EqualTo(1),
                        $"Tenant '{probe}' should have its per-iteration marker row after the perf smoke.");
                }
            }
            finally
            {
                DropTenantSchemas(tenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // ----- Helpers (shape matches PG SchemaTemplateHappyPathTests) ------------------------------

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

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant}\" CASCADE;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"CREATE SCHEMA \"{tenant}\";";
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

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant}\" CASCADE;";
            cmd.ExecuteNonQuery();
        }

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
