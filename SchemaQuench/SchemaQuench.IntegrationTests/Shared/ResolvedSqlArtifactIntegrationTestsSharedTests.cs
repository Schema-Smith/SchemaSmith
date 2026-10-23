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
using System.Linq;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests verifying the resolved-SQL artifact feature.
/// Three scenarios: artifact written to ArtifactPath (path in log, raw SQL not in log),
/// ScrubArtifacts is deprecated and ignored, and the artifact is re-runnable.
/// </summary>
[Category("Integration")]
public abstract class ResolvedSqlArtifactIntegrationTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract IConfigurationRoot FixtureConfig { get; }
    protected abstract string ProductPlatformFolder { get; }

    private const string ProductName = "ArtifactProbeProduct";
    private const string Marker = "artifact_probe_marker";
    private const string SensitiveValue = "sup3rs3cr3t_probe";

    private ILog _errorLog = null!;
    private ILog _progressLog = null!;
    private IEnvironment _environment = null!;
    private string _mainDb = null!;
    private string _adminConnectionString = null!;
    private string _artifactDir = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _errorLog = Substitute.For<ILog>();
        _progressLog = Substitute.For<ILog>();
        _environment = Substitute.For<IEnvironment>();

        _mainDb = MainDb;
        _adminConnectionString = BaseConnectionString + "Database=information_schema;";
    }

    [SetUp]
    public void SetUp()
    {
        _artifactDir = Path.Combine(Path.GetTempPath(), $"SchemaQuench_Artifact_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_artifactDir))
                Directory.Delete(_artifactDir, true);
        }
        catch
        {
            // Ignore cleanup errors; test isolation is more important than a clean temp dir.
        }
    }

    /// <summary>
    /// A failing user script writes the token-expanded SQL to a file in ArtifactPath.
    /// The progress log references the path; neither the progress log nor the error log
    /// contains the raw SQL marker — proving the SQL-leak is closed end-to-end.
    /// </summary>
    [Test]
    public void FailingScript_WritesArtifactToArtifactPath_LogHasPath_NotRawSql()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            var errorLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, errorLogLines);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "At least one .sql artifact file must be written to ArtifactPath on failure.");

                var artifactContent = File.ReadAllText(artifactFiles[0]);
                Assert.That(artifactContent, Does.Contain(Marker),
                    "Artifact must contain the distinctive SQL marker — proving resolved SQL is in the file.");

                var progressOutput = string.Join("\n", progressLogLines);
                Assert.That(progressOutput, Does.Contain("Resolved SQL written to:"),
                    "Progress log must contain the 'Resolved SQL written to:' reference.");
                Assert.That(progressOutput, Does.Contain(_artifactDir),
                    "Progress log must contain the artifact directory path.");
                Assert.That(progressOutput, Does.Not.Contain(Marker),
                    "Progress log must NOT contain the raw SQL marker — SQL must stay in the file, not the log.");

                var errorOutput = string.Join("\n", errorLogLines);
                Assert.That(errorOutput, Does.Not.Contain(Marker),
                    "Error log must NOT contain the raw SQL marker.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // The name the deploy identifies the database by, which is the server's: lowercase under lower_case_table_names=1.
    private string StoredDatabaseName()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{_mainDb}'";
        return cmd.ExecuteScalar()?.ToString() ?? _mainDb;
    }

    // #576: a run's start removes the previous run's files for its own product and target, and nothing else.
    [Test]
    public void ARun_DeletesThePreviousRunsFilesForItsTarget_AndLeavesEveryOtherFile()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);
            var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
            cfg["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            cfg["ArtifactPath"] = _artifactDir;
            cfg["WriteDebugSql"] = "true";
            var server = cfg["Target:Server"];
            var db = StoredDatabaseName();
            const string otherRun = "00000000000000000000000000000000";
            var previous = Path.Join(_artifactDir, "SchemaQuench - Previous run.sql");
            var otherDb = Path.Join(_artifactDir, "SchemaQuench - Other database.sql");
            File.WriteAllText(previous, $"-- SchemaSmith debug SQL | Product: {ProductName} | Server: {server} | Database: {db} | Schema:  | Run: {otherRun}");
            File.WriteAllText(otherDb, $"-- SchemaSmith debug SQL | Product: {ProductName} | Server: {server} | Database: not_{db} | Schema:  | Run: {otherRun}");
            var otherProduct = Path.Join(_artifactDir, "SchemaQuench - Other product.sql");
            File.WriteAllText(otherProduct, $"-- SchemaSmith debug SQL | Product: Not{ProductName} | Server: {server} | Database: {db} | Schema:  | Run: {otherRun}");
            // The product's own scripts run against each engine's initial database; one from another server is not this run's.
            var otherServer = new[] { "master", "postgres", "mysql", "information_schema", "" }
                .Select((initDb, i) => (Path: Path.Join(_artifactDir, $"SchemaQuench - Other server {i}.sql"), Owner: initDb)).ToList();
            foreach (var (path, initDb) in otherServer)
                File.WriteAllText(path, $"-- SchemaSmith debug SQL | Product: {ProductName} | Server: not-{server} | Database: {initDb} | Schema:  | Run: {otherRun}");

            try
            {
                RunSchemaQuench();

                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(previous), Is.False, "the previous run's file for this target is deleted");
                    Assert.That(File.Exists(otherDb), Is.True, "another database's file is not this run's to delete");
                    Assert.That(File.Exists(otherProduct), Is.True, "another product's file is not this run's to delete");
                    Assert.That(otherServer.Where(f => !File.Exists(f.Path)).Select(f => f.Owner), Is.Empty, "another server's product-script files stay");
                });
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // #576: a clean run deletes its own files; another run's, and a file without the owner line, stay.
    [Test]
    public void ACleanRun_DeletesItsOwnFiles_AndLeavesEveryOtherFile()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);
            var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
            cfg["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ValidProduct");
            cfg["ArtifactPath"] = _artifactDir;
            cfg["WriteDebugSql"] = "true";
            var mine = Path.Join(_artifactDir, "SchemaQuench - Mine.sql");
            var otherRun = Path.Join(_artifactDir, "SchemaQuench - Other run.sql");
            var unowned = Path.Join(_artifactDir, "SchemaQuench - Hand written.sql");
            File.WriteAllText(mine, Schema.Utility.ResolvedSqlArtifactWriter.OwnerLine("ValidProduct", "elsewhere", "elsewhere", ""));
            File.WriteAllText(otherRun, "-- SchemaSmith debug SQL | Product: ValidProduct | Server: elsewhere | Database: elsewhere | Schema:  | Run: 00000000000000000000000000000000");
            File.WriteAllText(unowned, "SELECT 1;");

            try
            {
                RunSchemaQuench();

                // 0, or 1 where the server downgrades the package's CHECK constraint (MySQL below 8.0.16): either way the run
                // completed, which is the case under test.
                _environment.Received(1).Exit(Arg.Is<int>(code => code == 0 || code == 1));
                Assert.Multiple(() =>
                {
                    Assert.That(Directory.GetFiles(_artifactDir, "*.sql").Select(Path.GetFileName).OrderBy(n => n),
                        Is.EqualTo(new[] { "SchemaQuench - Hand written.sql", "SchemaQuench - Other run.sql" }),
                        "every file this run wrote is gone, and nothing else is");
                });
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // #576: without WriteDebugSql no .sql file is written -- every deploy used to write one per table pass into the working
    // folder -- and the failure says how to get it.
    [Test]
    public void FailingScript_WithoutWriteDebugSql_WritesNoFile_AndSaysHowToGetIt()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);
                Assert.That(Directory.GetFiles(_artifactDir, "*.sql"), Is.Empty, "no file without WriteDebugSql");
                Assert.That(string.Join("\n", progressLogLines), Does.Contain("Rerun with --WriteDebugSql"),
                    "the failure says how to get the file");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// Failure Triage (#338): a tenant-scope failure emits the loud live <c>*** FAILED</c> banner
    /// and an end-of-run phase-grouped roll-up (both echoed to the progress log), naming the failed
    /// scope and template. The plain-text roll-up is the triage complement to the resolved-SQL
    /// artifact — it tells the operator WHICH target failed without grepping the interleaved stream.
    /// </summary>
    [Test]
    public void FailureTriage_TenantFailure_EmitsBannerAndRollup()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            var failureLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, null);
            var failureLog = Substitute.For<ILog>();
            failureLog.When(l => l.Info(Arg.Any<object>()))
                .Do(ci => failureLogLines.Add(ci.Arg<object>().ToString()!));
            LogFactory.Register("FailureLog", failureLog);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                // Live greppable banner in the progress stream.
                var progressOutput = string.Join("\n", progressLogLines);
                Assert.That(progressOutput, Does.Contain("*** FAILED [Template:"),
                    "A loud live *** FAILED banner must name the failed template scope.");

                // Phase-grouped end-of-run roll-up in the FailureLog (SchemaQuench - Failures.log).
                var failureOutput = string.Join("\n", failureLogLines);
                Assert.That(failureOutput, Does.Contain("failure(s):"),
                    "The end-of-run roll-up header (N failure(s): …) must be written to the FailureLog.");
                Assert.That(failureOutput, Does.Contain("─── FAILED"),
                    "The roll-up must render a per-failure entry in the FailureLog.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// ScrubArtifacts=true is deprecated and ignored (#576): the file holds the real token values.
    /// </summary>
    [Test]
    public void FailingScript_ScrubArtifacts_IsDeprecated_TheFileHoldsTheRealValues()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "Artifact file must be written even when ScrubArtifacts=true.");

                var artifactContent = File.ReadAllText(artifactFiles[0]);
                Assert.That(artifactContent, Does.Contain(SensitiveValue),
                    "#576: a scrubbed file left runnable SQL that wrote the placeholder; the file holds the real values now.");
                Assert.That(artifactContent, Does.Contain("real values"), "and its header says so");
                Assert.That(artifactContent, Does.Contain("us-east"),
                    "Non-sensitive token value (Region) must remain visible in the artifact.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// The written artifact is re-runnable: reading it back, stripping comment lines and GO
    /// separators, then executing the SQL against the same server produces the same class of
    /// error (unknown table), proving the artifact faithfully reproduces the failure.
    /// </summary>
    [Test]
    public void FailingScript_ArtifactIsRerunnable_ProducesSameError()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0), "Artifact must exist.");

                var artifactSql = ExtractExecutableSql(File.ReadAllText(artifactFiles[0]));
                Assert.That(artifactSql, Is.Not.Empty, "Extracted SQL from artifact must be non-empty.");

                using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_adminConnectionString);
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = artifactSql;

                Exception caughtEx = null;
                try { cmd.ExecuteNonQuery(); } catch (Exception ex) { caughtEx = ex; }
                Assert.That(caughtEx, Is.Not.Null, "Re-running the artifact must throw an exception.");
                Assert.That(caughtEx.Message, Does.Contain("artifact_probe_nonexistent_object"),
                    "Re-running the artifact must fail with the same unknown-table error referencing the probe object name.");
                conn.Close();
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // ----- Generated-DDL failure (#327 S4.2) ------------------------------------------------------

    /// <summary>
    /// A generated-DDL failure (index referencing a nonexistent column, thrown by the server-side
    /// SchemaSmith_IndexOnlyQuench proc) must surface with the same "Resolved SQL written to:"
    /// wording as user-script / data-delivery failures — not the legacy "Debug Script:" wording —
    /// and the failure must still produce a written artifact.
    /// </summary>
    [Test]
    public void GeneratedDdlFailure_SurfacesUnifiedWording_AndWritesArtifact()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, "DdlArtifactProbeProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "At least one .sql artifact file must be written to ArtifactPath for the generated-DDL failure.");

                var progressOutput = string.Join("\n", progressLogLines);
                Assert.That(progressOutput, Does.Contain("Resolved SQL written to:"),
                    "Generated-DDL failure must surface with the unified 'Resolved SQL written to:' wording, not 'Debug Script:'.");
                Assert.That(progressOutput, Does.Not.Contain("Debug Script:"),
                    "The legacy 'Debug Script:' wording must no longer appear.");

                // A table step's file reads the parse step's temp tables, so on its own it failed on a missing one.
                string[] tableSteps = ["Quench Missing Tables And Columns", "Quench Modified Tables", "Quench Table Features", "Quench Indexes", "Quench Foreign Keys"];
                var stepFiles = artifactFiles.Where(f => tableSteps.Any(step => Path.GetFileName(f).StartsWith("SchemaQuench - " + step))).ToList();
                Assert.That(stepFiles, Is.Not.Empty, "the failing index step writes its file; written: " + string.Join(", ", artifactFiles.Select(Path.GetFileName)));
                foreach (var stepFile in stepFiles)
                {
                    var lines = File.ReadAllLines(stepFile);
                    Assert.That(lines[0], Does.StartWith(ResolvedSqlArtifactWriter.OwnerLinePrefix), "the owner line stays first, for the cleanup that reads it");
                    Assert.That(lines, Does.Contain(DatabaseQuench.ParsedTablesNote), $"{Path.GetFileName(stepFile)} says what has to run before it");
                }
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // ----- Product-level script failure (#327 S4.3) -----------------------------------------------

    /// <summary>
    /// A failing PRODUCT-level Before script (Product.json ScriptFolders, not a template
    /// MigrationScripts/Before script) must write a resolved-SQL artifact and surface it with the
    /// same "Resolved SQL written to:" wording as template scripts / data delivery / generated DDL.
    /// The Bogus template's empty DatabaseIdentificationScript makes it a no-op, so only the
    /// product-level Before script executes.
    /// </summary>
    [Test]
    public void ProductBeforeScriptFailure_WritesArtifactToArtifactPath_LogHasPath_NotRawSql()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            var errorLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, errorLogLines);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, "ProductScriptArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();
                _environment.Received(1).Exit(2);
                _progressLog.Received(1).Error("FAILED: " + "Product script quench FAILED");

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "At least one .sql artifact file must be written to ArtifactPath for the product-level Before script failure.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Contain("product_before_artifact_probe_marker"),
                    "Artifact must contain the distinctive product-level Before script marker.");

                var progressOutput = string.Join("\n", progressLogLines);
                Assert.That(progressOutput, Does.Contain("Resolved SQL written to:"),
                    "Progress log must contain the 'Resolved SQL written to:' reference for the product-level script failure.");
                Assert.That(progressOutput, Does.Contain(_artifactDir),
                    "Progress log must contain the artifact directory path.");
                Assert.That(progressOutput, Does.Not.Contain("product_before_artifact_probe_marker"),
                    "Progress log must NOT contain the raw SQL marker — SQL must stay in the file, not the log.");

                var errorOutput = string.Join("\n", errorLogLines);
                Assert.That(errorOutput, Does.Not.Contain("product_before_artifact_probe_marker"),
                    "Error log must NOT contain the raw SQL marker.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // ----- Validation-script failure (#327 S4.4) --------------------------------------------------

    /// <summary>
    /// A failing BaselineValidationScript (returns false, throwing "Invalid baseline for this
    /// release") must write a resolved-SQL artifact and surface it with the same
    /// "Resolved SQL written to:" wording as every other script-failure surface.
    /// </summary>
    [Test]
    public void BaselineValidationScriptFailure_WritesArtifactToArtifactPath_LogHasPath_NotRawSql()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            var errorLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, errorLogLines);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, "BaselineValidationArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "At least one .sql artifact file must be written to ArtifactPath for the BaselineValidationScript failure.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Contain("baseline_validation_artifact_probe_marker"),
                    "Artifact must contain the distinctive BaselineValidationScript marker.");

                var progressOutput = string.Join("\n", progressLogLines);
                Assert.That(progressOutput, Does.Contain("Resolved SQL written to:"),
                    "Progress log must contain the 'Resolved SQL written to:' reference for the BaselineValidationScript failure.");
                Assert.That(progressOutput, Does.Contain(_artifactDir),
                    "Progress log must contain the artifact directory path.");
                Assert.That(progressOutput, Does.Not.Contain("baseline_validation_artifact_probe_marker"),
                    "Progress log must NOT contain the raw SQL marker — SQL must stay in the file, not the log.");

                var errorOutput = string.Join("\n", errorLogLines);
                Assert.That(errorOutput, Does.Not.Contain("baseline_validation_artifact_probe_marker"),
                    "Error log must NOT contain the raw SQL marker.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// A failing VersionStampScript must write a resolved-SQL artifact and surface it with the
    /// same "Resolved SQL written to:" wording as every other script-failure surface.
    /// </summary>
    [Test]
    public void VersionStampScriptFailure_WritesArtifactToArtifactPath_LogHasPath_NotRawSql()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            var errorLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, errorLogLines);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, "VersionStampArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["WriteDebugSql"] = "true";
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "At least one .sql artifact file must be written to ArtifactPath for the VersionStampScript failure.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Contain("version_stamp_artifact_probe_marker"),
                    "Artifact must contain the distinctive VersionStampScript marker.");

                var progressOutput = string.Join("\n", progressLogLines);
                Assert.That(progressOutput, Does.Contain("Resolved SQL written to:"),
                    "Progress log must contain the 'Resolved SQL written to:' reference for the VersionStampScript failure.");
                Assert.That(progressOutput, Does.Contain(_artifactDir),
                    "Progress log must contain the artifact directory path.");
                Assert.That(progressOutput, Does.Not.Contain("version_stamp_artifact_probe_marker"),
                    "Progress log must NOT contain the raw SQL marker — SQL must stay in the file, not the log.");

                var errorOutput = string.Join("\n", errorLogLines);
                Assert.That(errorOutput, Does.Not.Contain("version_stamp_artifact_probe_marker"),
                    "Error log must NOT contain the raw SQL marker.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["WriteDebugSql"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // ----- Helpers -------------------------------------------------------------------------------

    private void SetupSharedMocks(List<string> progressCapture, List<string> errorCapture)
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();

        FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);

        if (progressCapture != null)
        {
            _progressLog.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => progressCapture.Add(ci.Arg<object>().ToString()!));
        }

        if (errorCapture != null)
        {
            _errorLog.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => errorCapture.Add(ci.Arg<object>().ToString()!));
        }

        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    /// <summary>
    /// Strips the artifact comment header (lines starting with <c>--</c>) and <c>GO</c>
    /// separator lines, returning the raw SQL batches joined for execution.
    /// </summary>
    private static string ExtractExecutableSql(string artifactContent)
    {
        var lines = artifactContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var executableLines = lines
            .Where(l => !l.TrimStart().StartsWith("--") && !l.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join(Environment.NewLine, executableLines);
    }
}
