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
using System.Data;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// TemplateTargets integration tests for MySQL — database-axis only. MySQL has no schema/database
/// distinction, so the schema axis is excluded entirely; the database axis is its sole provisioning
/// surface. Tests cover the same three behaviors that SQL Server + PostgreSQL get on the DB axis:
/// pure enumeration override (existing DB), <c>CreateIfMissing: true</c> provisioning, and
/// <c>CreateIfMissing: false</c> skip-missing.
/// </summary>
public abstract class TemplateTargetsHappyPathTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ProductPlatformFolder { get; }

    private const string ProductName = "TemplateTargetsDbAxisProduct";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;

    protected TemplateTargetsHappyPathTestsSharedTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        // MySQL admin DB is information_schema per ProductQuench.GetInitDatabase. ProductQuench
        // composes the admin command via GetCommandForAdminDb, which re-targets a
        // --ConnectionString override through ConnectionString.RetargetDatabase when active so
        // CREATE DATABASE runs against information_schema regardless of the override's Database=
        // setting. These tests don't exercise the override path; they build their own admin
        // connection string here for setup/assertions.
        _connectionString = ConnectionString.Build(Platform, config["Target:Server"], "information_schema",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    [Test]
    public void DatabaseOverrideExistingDb_ReplacesIdentificationScript()
    {
        // Pure enumeration override on the DB axis: existing DB → no provisioning, deployment
        // runs against it. The DatabaseIdentificationScript in TemplateTargetsDbAxisProduct uses
        // the validator-recommended CONFIG-DRIVEN placeholder (returns no rows) so the override
        // IS the universe.
        var existingDb = MakeTransientDbName("ttdb_exists");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            CreateTransientDb(existingDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Body:Databases:0"] = existingDb;

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Source disclosure still names the override origin, now as the per-template rollup
                // rather than a line per work unit (the per-unit detail moved to the report).
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains("Template 'Body'") &&
                    s.Contains("db: TemplateTargets:Body:Databases")));

                // The DB still exists (no provisioning needed, none done) and the marker table
                // was deployed inside it.
                Assert.That(DatabaseExists(existingDb), Is.True);
                Assert.That(TableExistsInDb(existingDb, "marker"), Is.True,
                    "Body template's marker table must have been deployed inside the existing DB.");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(existingDb);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void DatabaseOverrideWithCreateIfMissing_ProvisionsMissingDbAndDeploys()
    {
        // CreateIfMissing: true → admin connection retargets to information_schema, issues
        // `CREATE DATABASE IF NOT EXISTS \`<name>\``, then quench runs inside the freshly-
        // provisioned DB. The marker table lands; the SeedMarker migration runs.
        var transientDb = MakeTransientDbName("ttdb_create");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            DropTransientDb(transientDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Body:Databases:0"] = transientDb;
            config["Target:TemplateTargets:Body:CreateIfMissing"] = "true";

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // MySQL provisioner log line uses backtick-quoted form: "Creating database
                // `X` (CreateIfMissing: true)".
                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains($"Creating database `{transientDb}`") &&
                    s.Contains("CreateIfMissing: true")));

                Assert.That(DatabaseExists(transientDb), Is.True,
                    "CreateIfMissing: true must provision the missing DB.");
                Assert.That(TableExistsInDb(transientDb, "marker"), Is.True,
                    "Body template deployment must have run inside the newly-provisioned DB.");
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
        // CreateIfMissing: false (default) → missing DB is SKIPPED with an info log; no CREATE.
        // Mix one pre-created DB + one missing DB so the work-unit list isn't empty (which
        // would trip RequireAtLeastOneTarget: true — the template default).
        var existingDb = MakeTransientDbName("ttdb_skip_exists");
        var missingDb = MakeTransientDbName("ttdb_skip_missing");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ClearCheckpointsForProduct();
            CreateTransientDb(existingDb);
            DropTransientDb(missingDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Body:Databases:0"] = existingDb;
            config["Target:TemplateTargets:Body:Databases:1"] = missingDb;
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

                // Existing DB still gets its template deployment.
                Assert.That(TableExistsInDb(existingDb, "marker"), Is.True,
                    "Existing DB in a mixed override must still deploy.");
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                DropTransientDb(existingDb);
                DropTransientDb(missingDb);
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

    // DB-axis tests deploy into either freshly-provisioned or test-managed DBs that have no
    // SchemaSmith helpers; the rest of the deployment pipeline depends on them. Run with full
    // kindling so the test target gets the helpers before the rest of the work runs. (Other
    // MySQL integration tests skip kindling because their fixture pre-kindles MainDb; tests
    // touching new DBs don't have that luxury.)
    private static void RunSchemaQuench() => Program.Main(System.Array.Empty<string>());

    private static void ClearTargetFilters(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTargetFilters(config);

    private static void ClearTemplateTargets(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTemplateTargets(config);

    private static void ClearCheckpointsForProduct()
    {
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);
    }

    private static string MakeTransientDbName(string prefix)
    {
        // Lowercase for case-insensitive MySQL filesystems (Linux); keeps the assertion path stable.
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"{prefix}_{unique}".ToLowerInvariant();
    }

    private void CreateTransientDb(string databaseName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{databaseName}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private bool DatabaseExists(string databaseName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = databaseName;
        cmd.Parameters.Add(p);
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt64(result) > 0;
    }

    private bool TableExistsInDb(string databaseName, string tableName)
    {
        if (!DatabaseExists(databaseName)) return false;
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(databaseName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.TABLES " +
                          $"WHERE TABLE_SCHEMA = '{databaseName}' AND TABLE_NAME = '{tableName}'";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt64(result) > 0;
    }

    private void DropTransientDb(string databaseName)
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
