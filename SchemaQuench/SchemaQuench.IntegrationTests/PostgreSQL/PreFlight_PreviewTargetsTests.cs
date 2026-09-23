// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Integration tests for <c>RunPreFlight(previewTargets: true)</c> on PostgreSQL.
/// Mirrors the SQL Server fixture. Each test operates in read-only preview mode: no DDL is deployed,
/// no databases or schemas are created (except the ghost-DB scenario that asserts the contrary),
/// and the real connection to the PostgreSQL container is exercised.
/// <para>
/// Fixture products used:
/// <list type="bullet">
///   <item><c>SchemaTemplateProduct</c> — Shared (regular DB template against TestMain) +
///   TenantBody (schema template discovering schemas from public.schematemplate_tenants).</item>
/// </list>
/// </para>
/// </summary>
[TestFixture]
[Category("PostgreSQL")]
[NonParallelizable]
public class PreFlight_PreviewTargetsTests
{
    private const string ProductName = "SchemaTemplateProduct";
    private const string SharedTemplate = "Shared";
    private const string TenantBodyTemplate = "TenantBody";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public PreFlight_PreviewTargetsTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [SetUp]
    public void SetUp()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    [TearDown]
    public void TearDown()
    {
        LogFactory.Clear();
        FactoryContainer.Unregister<IEnvironment>();
    }

    [Test]
    public void PreviewTargets_DbTemplateMatchesExistingDb_ReturnsTrueAndListsDb()
    {
        // Scenario 1: a template matching ≥1 database → preview lists them,
        // RunPreFlight returns true, Failed false.
        // SchemaTemplateProduct.Shared targets TestMain (always exists).
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            // Scope to Shared only so TenantBody (schema template requiring seeded data) doesn't run.
            config["Target:Templates:0"] = SharedTemplate;
            // Override Databases to TestMain — exercises the TemplateTargets preview path.
            config["Target:TemplateTargets:Shared:Databases:0"] = _mainDb;

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);

                // Preview report must mention the database.
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains($"db: {_mainDb}")));
                _errorLog.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
            }
            finally
            {
                config["Target:Templates:0"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void PreviewTargets_SchemaTemplate_ListsDiscoveredSchemasUnderDb()
    {
        // Scenario 2: a schema template → preview lists discovered schemas under each database.
        // TenantBody is a schema template (DatabaseIdentificationScript + SchemaIdentificationScript).
        // Override TenantBody:Schemas via TemplateTargets to make the test self-contained.
        const string tenantAlpha = "pt_alpha";
        const string tenantBeta = "pt_beta";

        lock (FactoryContainer.SharedLockObject)
        {
            EnsureSchemasExist(tenantAlpha, tenantBeta);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = TenantBodyTemplate;
            config["Target:TemplateTargets:TenantBody:Schemas:0"] = tenantAlpha;
            config["Target:TemplateTargets:TenantBody:Schemas:1"] = tenantBeta;

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);

                // Preview report must list both schemas.
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(tenantAlpha)));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(tenantBeta)));
                _errorLog.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
            }
            finally
            {
                config["Target:Templates:0"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
                DropSchemasIfExist(tenantAlpha, tenantBeta);
            }
        }
    }

    [Test]
    public void PreviewTargets_RequiredTemplateMatchesNothing_ReturnsFalseAndFailed()
    {
        // Scenario 3: RequireAtLeastOneTarget template matching nothing → returns false, Failed true.
        // Shared template has RequireAtLeastOneTarget: true (default). Override Databases to a
        // non-existent DB with no CreateIfMissing (default false) → unit is dropped → 0 work units.
        var ghostDb = $"ghost_{Guid.NewGuid():N}".Substring(0, 32).ToLowerInvariant();

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = SharedTemplate;
            config["Target:TemplateTargets:Shared:Databases:0"] = ghostDb;
            // CreateIfMissing absent — defaults false: missing DB is skipped → 0 work units.

            try
            {
                Assert.That(DatabaseExists(ghostDb), Is.False, "Ghost DB must not exist before the test.");

                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.False);
                Assert.That(pq.Failed, Is.True);

                // The progress log must surface the required-template miss.
                _progressLog.Received().Error(Arg.Is<string>(s =>
                    s.Contains("FAIL") && s.Contains("required")));
            }
            finally
            {
                config["Target:Templates:0"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void PreviewTargets_CreateIfMissingMiss_ReportsWouldCreateButDoesNotCreate()
    {
        // Scenario 4: TemplateTargets with CreateIfMissing: true naming a non-existent database →
        // preview reports "would be created", RunPreFlight returns true (would-create is not a failure),
        // AND the database does not exist afterward.
        var ghostDb = $"ghost_{Guid.NewGuid():N}".Substring(0, 32).ToLowerInvariant();

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:Templates:0"] = SharedTemplate;
            config["Target:TemplateTargets:Shared:Databases:0"] = ghostDb;
            config["Target:TemplateTargets:Shared:CreateIfMissing"] = "true";

            try
            {
                Assert.That(DatabaseExists(ghostDb), Is.False, "Ghost DB must not exist before the test.");

                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True, "would-create is not a failure — preview must return true.");
                Assert.That(pq.Failed, Is.False);

                // Preview report must surface "would be created" annotation.
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("would be created")));

                // Ghost DB must NOT exist — preview is read-only.
                Assert.That(DatabaseExists(ghostDb), Is.False,
                    "Preview must not create the database.");
            }
            finally
            {
                config["Target:Templates:0"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
                DropTransientDbIfExists(ghostDb);
            }
        }
    }

    [Test]
    public void PreviewTargets_NoDdlDeployed_SentinelObjectAbsentAfterPreview()
    {
        // Scenario 5: no DDL deployed by a preview run — the SentinelSkipProduct's sentinel function
        // must be absent after RunPreFlight(previewTargets: true). Use a fresh ghost DB via
        // CreateIfMissing so the function (which would land in a real quench) has nowhere to exist.
        // Assert the object is absent in the main DB too (belt-and-suspenders).
        var ghostDb = $"ghost_{Guid.NewGuid():N}".Substring(0, 32).ToLowerInvariant();

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "SentinelSkipProduct");
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Main:Databases:0"] = ghostDb;
            config["Target:TemplateTargets:Main:CreateIfMissing"] = "true";

            try
            {
                Assert.That(DatabaseExists(ghostDb), Is.False, "Ghost DB must not exist before the test.");

                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                // Preview returns true (would-create is not a failure).
                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);

                // Ghost DB must NOT have been created (preview is read-only).
                Assert.That(DatabaseExists(ghostDb), Is.False,
                    "Preview run must not create the ghost database.");

                // The sentinel function must not exist in the main DB either.
                Assert.That(FunctionExistsInDb(_mainDb, "public", "sentinelfunc"), Is.False,
                    "Preview must not deploy any DDL — SentinelFunc must be absent.");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
                DropTransientDbIfExists(ghostDb);
            }
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private static void ClearTargetFilters(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTargetFilters(config);

    private static void ClearTemplateTargets(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTemplateTargets(config);

    private bool DatabaseExists(string databaseName)
    {
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

    private bool FunctionExistsInDb(string databaseName, string schemaName, string functionName)
    {
        if (!DatabaseExists(databaseName)) return false;
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(databaseName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.routines " +
                          $"WHERE routine_schema = '{schemaName}' AND LOWER(routine_name) = '{functionName.ToLowerInvariant()}'";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }

    private void EnsureSchemasExist(params string[] schemas)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        foreach (var schema in schemas)
        {
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private void DropSchemasIfExist(params string[] schemas)
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            foreach (var schema in schemas)
            {
                cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
                cmd.ExecuteNonQuery();
            }
            conn.Close();
        }
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }

    private void DropTransientDbIfExists(string databaseName)
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $@"
SELECT pg_terminate_backend(pid)
  FROM pg_stat_activity
  WHERE datname = '{databaseName}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }
}
