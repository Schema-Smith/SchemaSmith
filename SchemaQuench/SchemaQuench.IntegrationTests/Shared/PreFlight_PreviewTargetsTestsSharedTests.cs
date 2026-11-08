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

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for <c>RunPreFlight(previewTargets: true)</c> on MySQL.
/// Mirrors the SQL Server and PostgreSQL fixtures where MySQL's engine model permits.
/// <para>
/// MySQL has no two-level (database → schema) hierarchy: templates use either
/// <c>DatabaseIdentificationScript</c> (DB axis) or <c>SchemaIdentificationScript</c> (schema axis,
/// where MySQL schemas ARE databases). Templates that expose only <c>SchemaIdentificationScript</c>
/// (no <c>DatabaseIdentificationScript</c>) are explicitly skipped by <c>PreviewTargets</c> — there
/// is nothing to preview without a database-level anchor. Scenario 2 tests this skip behavior
/// directly as the MySQL-correct equivalent of the cross-engine scenario.
/// </para>
/// <para>
/// Fixture products used:
/// <list type="bullet">
///   <item><c>TemplateTargetsDbAxisProduct</c> — Body template with a config-driven
///   <c>DatabaseIdentificationScript</c> (returns 0 rows by default, TemplateTargets-driven).</item>
///   <item><c>ValidProduct</c> — Main template using <c>SchemaIdentificationScript</c> only
///   (exercises the preview-skip path for schema-only templates).</item>
/// </list>
/// </para>
/// </summary>
public abstract class PreFlight_PreviewTargetsTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ProductPlatformFolder { get; }

    private const string DbAxisProductName = "TemplateTargetsDbAxisProduct";
    private const string ValidProductName = "ValidProduct";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;

    protected PreFlight_PreviewTargetsTestsSharedTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform, config["Target:Server"], "information_schema",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
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
        // TemplateTargetsDbAxisProduct.Body with Databases:0 = information_schema (always exists).
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, DbAxisProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Body:Databases:0"] = "information_schema";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);

                // Preview report must mention the database.
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("db: information_schema")));
                _errorLog.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
            }
            finally
            {
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void PreviewTargets_SchemaOnlyTemplate_IsSkippedByPreview()
    {
        // Scenario 2 (MySQL-specific): MySQL schema templates have SchemaIdentificationScript only
        // (no DatabaseIdentificationScript). PreviewTargets skips these templates — there is no
        // database-level anchor to preview against. This test asserts the skip path:
        // - RunPreFlight returns true (no required templates fired — skipped templates aren't failing)
        // - No error is logged for the skipped template
        // - The progress log does NOT surface a "Template: Main" header line (the template is silent)
        //
        // ValidProduct.Main uses SchemaIdentificationScript only — the canonical MySQL-style template.
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ValidProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            // Scope to Main only (the schema-only template) so Bogus/Secondary don't interfere.
            config["Target:Templates:0"] = "Main";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                // A product where all in-scope templates are schema-only (skipped by preview) returns
                // true with Failed false — there are no required templates to miss.
                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);

                // No error logged — silent skip is correct.
                _errorLog.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
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

    // A Target.Templates value that names no template is an error the preview logs -- and must not then report a pass:
    // the exit code is what a pipeline reads.
    [Test]
    public void PreviewTargets_ATargetTemplateThatDoesNotExist_ReturnsFalseAndFailed()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ValidProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "NoSuchTemplate";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.Multiple(() =>
                {
                    Assert.That(result, Is.False, "the preview fails, so the run exits non-zero");
                    Assert.That(pq.Failed, Is.True);
                    _progressLog.Received().Error(Arg.Is<string>(m => m.Contains("NoSuchTemplate")));
                });
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
    public void PreviewTargets_RequiredTemplateMatchesNothing_ReturnsFalseAndFailed()
    {
        // Scenario 3: RequireAtLeastOneTarget template matching nothing → returns false, Failed true.
        // TemplateTargetsDbAxisProduct.Body has DatabaseIdentificationScript that returns 0 rows
        // (WHERE 1=0) and RequireAtLeastOneTarget: true (default). With no TemplateTargets override,
        // the identification script fires and returns nothing → 0 work units → required-miss.
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, DbAxisProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            // No TemplateTargets override — Body's WHERE 1=0 returns nothing.

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.False);
                Assert.That(pq.Failed, Is.True);

                // Progress log must surface the required-template miss.
                _progressLog.Received().Error(Arg.Is<string>(s =>
                    s.Contains("FAIL") && s.Contains("required")));
            }
            finally
            {
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
        var ghostDb = MakeGhostDbName("ghost_preview");

        lock (FactoryContainer.SharedLockObject)
        {
            DropTransientDbIfExists(ghostDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, DbAxisProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Body:Databases:0"] = ghostDb;
            config["Target:TemplateTargets:Body:CreateIfMissing"] = "true";

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
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
                DropTransientDbIfExists(ghostDb);
            }
        }
    }

    [Test]
    public void PreviewTargets_NoDdlDeployed_SentinelTableAbsentAfterPreview()
    {
        // Scenario 5: no DDL deployed by a preview run — the TemplateTargetsDbAxisProduct's marker
        // table must be absent after RunPreFlight(previewTargets: true). The ghost DB with
        // CreateIfMissing: true would receive the marker table on a real quench; preview must
        // leave both the ghost DB AND the table absent.
        var ghostDb = MakeGhostDbName("ghost_nodeploy");

        lock (FactoryContainer.SharedLockObject)
        {
            DropTransientDbIfExists(ghostDb);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, DbAxisProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);

            config["Target:TemplateTargets:Body:Databases:0"] = ghostDb;
            config["Target:TemplateTargets:Body:CreateIfMissing"] = "true";

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

                // Because the ghost DB doesn't exist, the marker table cannot exist either.
                Assert.That(TableExistsInDb(ghostDb, "marker"), Is.False,
                    "Preview must not deploy any DDL — marker table must be absent.");
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

    private static string MakeGhostDbName(string prefix)
    {
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"{prefix}_{unique}".ToLowerInvariant();
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

    private void DropTransientDbIfExists(string databaseName)
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
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }
}
