// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.IntegrationTests.PostgreSQL.Profiling;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Phase 1 of the PG connection-discipline investigation (see
/// docs/plans/2026-05-22-pg-connection-discipline-investigation-plan.md in the Community repo).
/// Parametric workload — five runs, each varying tenant count × AllowParallel — captured to CSV
/// for analysis. Goal is to answer "where does the burst come from" by attributing peak demand
/// across test-setup / engine / test-cleanup categories.
///
/// <para>Tagged <c>[Explicit]</c> so it does NOT run in CI. Run manually:
/// <c>dotnet test --filter "FullyQualifiedName~SchemaTemplateConnectionProfileTests"</c>.
/// CSVs land under the test binary's profiling-runs/ subfolder.</para>
/// </summary>
[Category("PostgreSQL")]
public class SchemaTemplateConnectionProfileTests
{
    private const string ProductName = "SchemaTemplateProduct";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    private string _templatePath;
    private string _originalTemplateJson;
    private string _profilingRunsRoot;

    public SchemaTemplateConnectionProfileTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [OneTimeSetUp]
    public void CacheOriginalTemplate()
    {
        _templatePath = Path.Combine(
            TestHelper.GetTestProductPath("PostgreSQL", ProductName),
            "Templates", "TenantBody", "Template.json");
        _originalTemplateJson = File.ReadAllText(_templatePath);
        _profilingRunsRoot = Path.Combine(AppContext.BaseDirectory, "profiling-runs");
    }

    [OneTimeTearDown]
    public void RestoreOriginalTemplate()
    {
        if (!string.IsNullOrEmpty(_originalTemplateJson) && File.Exists(_templatePath))
            File.WriteAllText(_templatePath, _originalTemplateJson);
    }

    [Test]
    [Explicit("Connection profiling — 10 tenants, AllowParallel=true. Run on demand only.")]
    public void Run1_10Tenants_Parallel() => CaptureRun("r1_parallel_10", tenantCount: 10, allowParallel: true);

    [Test]
    [Explicit("Connection profiling — 50 tenants, AllowParallel=true. Run on demand only.")]
    public void Run2_50Tenants_Parallel() => CaptureRun("r2_parallel_50", tenantCount: 50, allowParallel: true);

    [Test]
    [Explicit("Connection profiling — 100 tenants, AllowParallel=true. Run on demand only.")]
    public void Run3_100Tenants_Parallel() => CaptureRun("r3_parallel_100", tenantCount: 100, allowParallel: true);

    [Test]
    [Explicit("Connection profiling — 200 tenants, AllowParallel=true. Run on demand only.")]
    public void Run4_200Tenants_Parallel() => CaptureRun("r4_parallel_200", tenantCount: 200, allowParallel: true);

    [Test]
    [Explicit("Connection profiling — 100 tenants, AllowParallel=false (serial baseline). Run on demand only.")]
    public void Run5_100Tenants_Serial() => CaptureRun("r5_serial_100", tenantCount: 100, allowParallel: false);

    private void CaptureRun(string runName, int tenantCount, bool allowParallel)
    {
        var tenants = Enumerable.Range(0, tenantCount)
            .Select(i => $"profile_{i:D3}")
            .ToArray();

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            SetAllowParallel(allowParallel);

            var recorder = new ProfilingConnectionRecorder();
            var listener = new NpgsqlEventCounterListener();
            FactoryContainer.Register<IDbConnectionFactory>(new ProfilingPostgreSqlConnectionFactory(recorder));

            try
            {
                ResetTrackingAndCreateTenantSchemas(tenants);

                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                    TestHelper.GetTestProductPath("PostgreSQL", ProductName);

                RunSchemaQuench();

                // Drain a moment of pool-counter samples after the quench completes so the trailing
                // edge of the busy → idle transition lands in the CSV. 200ms is generous.
                System.Threading.Thread.Sleep(200);

                DropTenantSchemas(tenants);
            }
            finally
            {
                FactoryContainer.Unregister<IDbConnectionFactory>();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();

                var runDir = Path.Combine(_profilingRunsRoot, runName);
                Directory.CreateDirectory(runDir);
                recorder.WriteCsv(Path.Combine(runDir, "connections.csv"));
                listener.WriteCsv(Path.Combine(runDir, "pool-counters.csv"));
                WriteSummary(Path.Combine(runDir, "summary.txt"), runName, tenantCount, allowParallel, recorder, listener);
                listener.Dispose();

                TestContext.Out.WriteLine($"Profiling CSVs for {runName} written to: {runDir}");
            }
        }
    }

    private void SetAllowParallel(bool value)
    {
        var json = _originalTemplateJson;
        var newValue = value ? "true" : "false";
        json = json.Replace("\"AllowParallel\": false", $"\"AllowParallel\": {newValue}");
        json = json.Replace("\"AllowParallel\": true", $"\"AllowParallel\": {newValue}");
        File.WriteAllText(_templatePath, json);
    }

    private static void WriteSummary(string path, string runName, int tenantCount, bool allowParallel,
        ProfilingConnectionRecorder recorder, NpgsqlEventCounterListener listener)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine($"Run: {runName}");
        writer.WriteLine($"  Tenant count: {tenantCount}");
        writer.WriteLine($"  AllowParallel: {allowParallel}");
        writer.WriteLine();
        writer.WriteLine("Wrapper-side counts (from ProfilingConnectionRecorder):");
        writer.WriteLine($"  Total opens:  {recorder.OpenCount}");
        writer.WriteLine($"  Total closes: {recorder.CloseCount}");
        writer.WriteLine($"  Peak concurrent opens: {recorder.PeakConcurrentOpens}");
        writer.WriteLine();
        writer.WriteLine($"Pool-counter samples (from NpgsqlEventCounterListener): {listener.SampleCount}");
        writer.WriteLine("  See pool-counters.csv for per-counter time series.");
    }

    // ----- Helpers (shape matches SchemaTemplatePerfSmokeTests / SchemaTemplateHappyPathTests) -----

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

    private static void ClearCheckpointsForProduct() =>
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);

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
}
