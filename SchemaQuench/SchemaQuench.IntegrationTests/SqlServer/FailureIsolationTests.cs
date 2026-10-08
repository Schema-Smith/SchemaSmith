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
using System.Linq;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Slice-4 failure isolation integration tests (design §5.5 and §10.4).
///
/// <para><b>ContinueOnSchemaFailure tests</b> use SchemaTemplateFailureProduct — a 5-tenant
/// fixture where one tenant ('tenant_boom') triggers a deliberate RAISERROR in Boom.sql.
/// AllowParallel is false so unit execution is deterministic.</para>
///
/// <para><b>ContinueOnDatabaseFailure tests</b> use SchemaTemplateDbFailureProduct — a fixture
/// whose SchemaIdentificationScript returns 'dbo' (a reserved name), causing SchemaDiscovery
/// to throw, which is classified as a DB-level failure. With a single-database fixture we
/// verify the continue / abort routing without requiring a second physical database.</para>
///
/// <para>Abort-mode variants mutate the shared fixture's Template.json for the duration of the
/// test and restore it in finally — no separate *AbortProduct fixture directories needed.</para>
/// </summary>
[Category("SqlServer")]
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
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    /// <summary>
    /// ContinueOnSchemaFailure: true (default). 5 tenants, one fails (tenant_boom via Boom.sql
    /// RAISERROR). The remaining 4 tenants complete. Exit code is non-zero. Failure summary
    /// names the failing tenant.
    /// </summary>
    [Test]
    public void ContinueOnSchemaFailure_True_OneFailure_OthersComplete_ExitNonZero()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", FailureProductName);

            try
            {
                RunSchemaQuench();

                // Non-zero exit (failure occurred).
                _environment.Received().Exit(2);

                // Exactly one error: the boom tenant.
                _progressLog.Received().Error(Arg.Is<string>(msg => msg.Contains("tenant_boom")));

                // The 4 non-boom tenants must complete.
                foreach (var tenant in new[] { "tenant_a", "tenant_b", "tenant_c", "tenant_d" })
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // The boom tenant must NOT have quenched successfully.
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
    /// Continuing past a failed template keeps the other templates deploying; it must not finish the product as if
    /// the deploy had succeeded. The After Product scripts and the version stamp each create a marker table: a clean
    /// run creates both (so the scripts are proven to work), and a run in which a tenant fails creates neither.
    /// </summary>
    [Test]
    public void AFailedTemplate_SkipsTheAfterProductScriptsAndTheVersionStamp()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var productPath = TestHelper.GetTestProductPath("SqlServer", FailureProductName);
            var productFile = Path.Join(productPath, "Product.json");
            var original = File.ReadAllText(productFile);
            var finalizeDir = Path.Join(productPath, "Finalize");
            const string afterMarker = "AfterProductProbe";
            const string stampMarker = "VersionStampProbe";

            try
            {
                Directory.CreateDirectory(finalizeDir);
                File.WriteAllText(Path.Join(finalizeDir, "Finalize.sql"),
                    $"CREATE TABLE [{{{{MainDB}}}}].dbo.{afterMarker} (Id INT)");
                File.WriteAllText(productFile, original
                    .Replace("\"ScriptFolders\": [],",
                        "\"ScriptFolders\": [ { \"FolderPath\": \"Finalize\", \"QuenchSlot\": \"After\" } ],")
                    .Replace("\"Platform\": \"MSSQL\"",
                        $"\"VersionStampScript\": \"CREATE TABLE [{{{{MainDB}}}}].dbo.{stampMarker} (Id INT)\",\n    \"Platform\": \"MSSQL\""));
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = productPath;

                SetupSharedMocks();
                DropMarkers(afterMarker, stampMarker);
                ResetTrackingAndCreateTenantSchemas(FiveTenants.Where(t => t != "tenant_boom"), FailureProductName);
                RunSchemaQuench();
                _environment.DidNotReceive().Exit(2);
                Assert.That(MarkerExists(afterMarker) && MarkerExists(stampMarker), Is.True,
                    "a clean run must run the After Product scripts and the version stamp, or the check below proves nothing");

                SetupSharedMocks();
                DropMarkers(afterMarker, stampMarker);
                ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
                RunSchemaQuench();
                _environment.Received().Exit(2);
                Assert.Multiple(() =>
                {
                    Assert.That(MarkerExists(afterMarker), Is.False, "After Product scripts ran after a template failed");
                    Assert.That(MarkerExists(stampMarker), Is.False, "the version stamp was written after a template failed");
                });
                _progressLog.Received(1).Error(Arg.Is<string>(m => m.Contains("Skipped the After Product scripts")));
            }
            finally
            {
                File.WriteAllText(productFile, original);
                if (Directory.Exists(finalizeDir)) Directory.Delete(finalizeDir, true);
                DropMarkers(afterMarker, stampMarker);
                DropTenantSchemas(FiveTenants, FailureProductName);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    private void DropMarkers(params string[] tables)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        foreach (var table in tables)
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    private bool MarkerExists(string table)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT CAST(CASE WHEN OBJECT_ID('dbo.{table}') IS NULL THEN 0 ELSE 1 END AS BIT)";
        return (bool)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// ContinueOnSchemaFailure: false. After the failure, the dispatcher stops dispatching
    /// new units for this template; in-flight units drain naturally. Subsequent templates
    /// (PostFailure) do not run. Exit invoked.
    /// </summary>
    [Test]
    public void ContinueOnSchemaFailure_False_FailureAbortsTemplate_SubsequentTemplatesDoNotRun()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("SqlServer", FailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", FailureProductName);

            var modifiedJson = originalJson.Replace(
                "\"AllowParallel\": false,",
                "\"AllowParallel\": false,\r\n    \"ContinueOnSchemaFailure\": false,");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                // Exit must have been called (abort).
                _environment.Received().Exit(2);

                // PostFailure template must NOT have been announced in the progress log.
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

    [Test]
    public void Schema_Template_ContinueOnSchemaFailure_False_Discovery_Failure_Aborts()
    {
        // Schema-template discovery failure (reserved-name guard rejects 'dbo') with
        // ContinueOnSchemaFailure: false. Under the per-template scope contract, a schema
        // template's failures — including discovery — are governed by ContinueOnSchemaFailure.
        // Run must abort and the next template (PostFailure) must NOT run.
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("SqlServer", DbFailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", DbFailureProductName);

            var modifiedJson = originalJson.Replace(
                "\"ScriptFolders\": []",
                "\"ContinueOnSchemaFailure\": false,\r\n    \"ScriptFolders\": []");
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
    /// A schema-kind failure (tenant_boom's Boom.sql RAISERROR) with ContinueOnDatabaseFailure:
    /// false and ContinueOnSchemaFailure: true (default) MUST continue — schema templates honor
    /// only the schema policy, not the DB one. The next template (PostFailure) runs. Exit code
    /// non-zero.
    /// </summary>
    [Test]
    public void Schema_Template_ContinueOnDatabaseFailure_Has_No_Effect_When_ContinueOnSchemaFailure_True()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("SqlServer", FailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(FiveTenants, FailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", FailureProductName);

            // Add ContinueOnDatabaseFailure:false while keeping ContinueOnSchemaFailure at its
            // default (true). The failure is schema-kind so the schema policy governs — should NOT abort.
            var modifiedJson = originalJson.Replace(
                "\"AllowParallel\": false,",
                "\"AllowParallel\": false,\r\n    \"ContinueOnDatabaseFailure\": false,");
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
    /// returns a reserved name ('dbo'), causing SchemaDiscovery to throw — a schema-scope
    /// failure (per the new per-template-scope contract). The run logs the error but continues
    /// to the PostFailure template. Exit code is non-zero.
    /// </summary>
    [Test]
    public void Schema_Template_Discovery_Failure_Continues_Under_Default_ContinueOnSchemaFailure()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", DbFailureProductName);

            try
            {
                RunSchemaQuench();

                // Exit code non-zero (DB-level failure).
                _environment.Received().Exit(2);

                // Error logged for the discovery failure.
                _progressLog.Received().Error(Arg.Is<string>(msg =>
                    msg.Contains("Schema discovery FAILED") || msg.Contains("TenantBody")));

                // PostFailure template DID run (continue mode keeps going after DB failure).
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
    /// continue — only the schema-scope policy governs. The next template (PostFailure) runs.
    /// This is the explicit regression test for the prior layered-bits behavior that incorrectly
    /// let ContinueOnDatabaseFailure abort schema-template discovery failures.
    /// </summary>
    [Test]
    public void Schema_Template_Discovery_Failure_Ignores_ContinueOnDatabaseFailure()
    {
        var templateJsonPath = Path.Combine(
            TestHelper.GetTestProductPath("SqlServer", DbFailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", DbFailureProductName);

            // ContinueOnDatabaseFailure: false would have aborted under the prior layered
            // model. Under the per-template-scope contract, it's ignored on schema templates.
            var modifiedJson = originalJson.Replace(
                "\"ScriptFolders\": []",
                "\"ContinueOnDatabaseFailure\": false,\r\n    \"ScriptFolders\": []");
            File.WriteAllText(templateJsonPath, modifiedJson);

            try
            {
                RunSchemaQuench();

                // Non-zero exit (discovery still fails).
                _environment.Received().Exit(2);

                // PostFailure template DID run — ContinueOnDatabaseFailure had no effect; the
                // default ContinueOnSchemaFailure: true governed and let the run continue.
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
            TestHelper.GetTestProductPath("SqlServer", DbFailureProductName),
            "Templates", "TenantBody", "Template.json");
        var originalJson = File.ReadAllText(templateJsonPath);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct(DbFailureProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", DbFailureProductName);

            var modifiedJson = originalJson.Replace(
                "\"ScriptFolders\": []",
                "\"ContinueOnSchemaFailure\": false,\r\n    \"ScriptFolders\": []");
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = $@"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{productName}';
IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '{productName}';
IF OBJECT_ID('dbo.Tenants', 'U') IS NOT NULL DELETE FROM dbo.Tenants;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, FiveTenants);

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = @"
IF OBJECT_ID('dbo.Tenants', 'U') IS NULL
    CREATE TABLE dbo.Tenants ([Name] NVARCHAR(128) NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO dbo.Tenants ([Name]) VALUES (N'{tenant}');";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    private void DropTenantSchemas(IEnumerable<string> tenants, string productName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        DropTenantSchemasInternal(cmd, tenants);

        cmd.CommandText = $@"
IF OBJECT_ID('dbo.Tenants', 'U') IS NOT NULL DROP TABLE dbo.Tenants;
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{productName}';
IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '{productName}';";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private static void DropTenantSchemasInternal(IDbCommand cmd, IEnumerable<string> tenants)
    {
        foreach (var tenant in tenants)
        {
            cmd.CommandText = $@"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + 'DROP TABLE [' + s.name + '].[' + t.name + '];' + CHAR(10)
  FROM sys.tables t
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
IF @sql <> '' EXEC sp_executesql @sql;
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }
    }

}
