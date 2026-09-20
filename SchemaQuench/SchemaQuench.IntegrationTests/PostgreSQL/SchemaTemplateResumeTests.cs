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
using System.IO;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Slice-3 audit Phase 4 — <c>--ResumeQuench</c> mid-schema-template-run coverage on PostgreSQL.
/// PostgreSQL parity for <c>SchemaQuench.IntegrationTests.SqlServer.SchemaTemplateResumeTests</c>.
/// Verifies the resume mechanism wires through correctly to schema-template iterations — the
/// checkpoint file path includes the iteration schema name (slice 2 filename change), and the
/// engine recognizes a seeded checkpoint on a per-iteration basis.
/// </summary>
[Category("PostgreSQL")]
public class SchemaTemplateResumeTests
{
    private const string ProductName = "SchemaTemplateProduct";

    private static readonly string[] DefaultTenants = ["tenant_acme", "tenant_beta", "tenant_globex"];

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;
    private string _checkpointDir;

    public SchemaTemplateResumeTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [SetUp]
    public void SetUp()
    {
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"SchemaQuench_SchemaTemplateResume_PG_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_checkpointDir))
                Directory.Delete(_checkpointDir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Test]
    public void Resume_Recognizes_Per_Tenant_Checkpoint_File_And_Logs_Resuming_From_Checkpoint()
    {
        // Design §5.11 (PG mirror): a schema iteration with an existing checkpoint emits the
        // "Resuming from checkpoint" log line. The checkpoint filename includes the iteration
        // schema name in the 5th slot (slice 2). Seed a checkpoint for tenant_acme and assert
        // the engine recognizes it on the per-iteration scope; sibling tenants must not show a
        // resume line (no checkpoint seeded for them).
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            config["CheckpointDirectory"] = _checkpointDir;

            try
            {
                // Seed a per-iteration checkpoint for tenant_acme. Only KindleForge is marked
                // complete — that step is suppressed via SkipKindlingForge anyway, and the rest
                // of the iteration runs fresh. The recognized "Resuming from checkpoint" log
                // is the assertion we're after.
                var product = Product.Load();
                var checkpointPath = Path.Combine(_checkpointDir,
                    $"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("TenantBody")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(_mainDb)}.{FileNameEncoder.Encode("tenant_acme")}.checkpoint");
                File.WriteAllText(checkpointPath, $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: TenantBody
# Server: {_server}
# Database: {_mainDb}
# SchemaName: tenant_acme
# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

[Completed Steps]
KindleForge

[Before Scripts]

[Object Scripts]

[After Tables Object Scripts]

[Between Tables And Keys Scripts]

[After Table Scripts]

[Table Data Scripts]

[After Scripts]
");

                RunSchemaQuench();

                // The Slice-3 contract: tenant_acme's iteration must recognize the per-tenant
                // checkpoint and emit "Resuming from checkpoint". Other tenants must NOT show
                // a resume line (no checkpoint seeded for them).
                _progressLog.Received(1).Info(Arg.Is<string>(s =>
                    s.Contains("[Schema: tenant_acme]") && s.Contains("Resuming from checkpoint")));

                foreach (var tenant in new[] { "tenant_beta", "tenant_globex" })
                {
                    _progressLog.DidNotReceive().Info(Arg.Is<string>(s =>
                        s.Contains($"[Schema: {tenant}]") && s.Contains("Resuming from checkpoint")));
                }

                // All three tenants still complete successfully — the partial checkpoint
                // doesn't strand any iteration. tenant_acme picks up after KindleForge; the
                // others run from scratch.
                foreach (var tenant in DefaultTenants)
                {
                    _progressLog.Received(1).Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");
                }
            }
            finally
            {
                config["CheckpointDirectory"] = null;
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
            }
        }
    }

    // ----- Helpers (shape matches PG SchemaTemplateHappyPathTests) ------------------------------

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        // Mirror SQL Server: set the resume flag on the mock IEnvironment so CommandLineParser
        // sees --ResumeQuench and the engine treats the seeded checkpoint as "resume from"
        // rather than "stale, ignore".
        _environment.CommandLine.Returns("--SkipKindlingForge --ResumeQuench");
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
}
