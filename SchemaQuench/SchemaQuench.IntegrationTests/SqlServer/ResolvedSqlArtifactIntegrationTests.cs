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
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Integration tests verifying the resolved-SQL artifact feature on SQL Server.
/// Three scenarios: artifact written to ArtifactPath (path in log, raw SQL not in log),
/// ScrubArtifacts=true masks sensitive token values, and the artifact is re-runnable.
/// </summary>
[Category("SqlServer")]
public class ResolvedSqlArtifactIntegrationTests
{
    private const string ProductName = "ArtifactProbeProduct";
    private const string Marker = "artifact_probe_marker";
    private const string SensitiveValue = "sup3rs3cr3t_probe";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private string _artifactDir = null!;

    public ResolvedSqlArtifactIntegrationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
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
                cfg["ScrubArtifacts"] = null;
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
                TestHelper.GetTestProductPath("SqlServer", ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;

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
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// When ScrubArtifacts=true, the sensitive token value (AdminPassword) is masked in the
    /// written artifact; a non-sensitive token's value (Region) still appears.
    /// </summary>
    [Test]
    public void FailingScript_ScrubArtifacts_MasksSensitiveTokenInArtifact()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "Artifact file must be written even when ScrubArtifacts=true.");

                var artifactContent = File.ReadAllText(artifactFiles[0]);
                Assert.That(artifactContent, Does.Not.Contain(SensitiveValue),
                    "Sensitive token value must be masked in the artifact when ScrubArtifacts=true.");
                Assert.That(artifactContent, Does.Contain("us-east"),
                    "Non-sensitive token value (Region) must remain visible in the artifact.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// The written artifact is re-runnable: reading it back, stripping comment lines and GO
    /// separators, then executing the SQL against the same server produces the same class of
    /// error (invalid object reference), proving the artifact faithfully reproduces the failure.
    /// </summary>
    [Test]
    public void FailingScript_ArtifactIsRerunnable_ProducesSameError()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = null;

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0), "Artifact must exist.");

                var artifactSql = ExtractExecutableSql(File.ReadAllText(artifactFiles[0]));
                Assert.That(artifactSql, Is.Not.Empty, "Extracted SQL from artifact must be non-empty.");

                using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = artifactSql;

                Exception caughtEx = null;
                try { cmd.ExecuteNonQuery(); } catch (Exception ex) { caughtEx = ex; }
                Assert.That(caughtEx, Is.Not.Null, "Re-running the artifact must throw an exception.");
                Assert.That(caughtEx.Message, Does.Contain("artifact_probe_nonexistent_object"),
                    "Re-running the artifact must fail with the same missing-object error referencing the probe object name.");
                conn.Close();
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    // ----- Generated-DDL failure (#327 S4.2) ------------------------------------------------------

    /// <summary>
    /// A generated-DDL failure (index referencing a nonexistent column, thrown by the
    /// server-side IndexOnlyQuench proc) must write a scrub-aware artifact and surface it with the
    /// same "Resolved SQL written to:" wording as user-script / data-delivery failures — not the
    /// legacy "Debug Script:" wording. DdlArtifactProbeProduct's table embeds a marker matching the
    /// product's AdminPassword token value directly in the generated @TableDefinitions JSON that
    /// IndexOnlyQuench receives, so the written artifact — and the scrub check below — exercise the
    /// real LogSqlScript code path (not just the CALL statement text).
    /// </summary>
    [Test]
    public void GeneratedDdlFailure_WritesScrubAwareArtifact_WithUnifiedWording()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var progressLogLines = new List<string>();
            SetupSharedMocks(progressLogLines, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "DdlArtifactProbeProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
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

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Contain("sup3rs3cr3t_ddl_probe"),
                    "Without ScrubArtifacts, the generated-DDL artifact must retain the raw resolved SQL.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// Same generated-DDL failure with ScrubArtifacts=true: the sensitive token value embedded in
    /// the generated @TableDefinitions JSON must be masked in the written artifact.
    /// </summary>
    [Test]
    public void GeneratedDdlFailure_ScrubArtifacts_MasksSensitiveTokenInArtifact()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "DdlArtifactProbeProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "Artifact file must be written even when ScrubArtifacts=true.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Not.Contain("sup3rs3cr3t_ddl_probe"),
                    "Sensitive token value must be masked in the generated-DDL artifact when ScrubArtifacts=true.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
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
                TestHelper.GetTestProductPath("SqlServer", "ProductScriptArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
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
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// Same product-level Before script failure with ScrubArtifacts=true: the sensitive product
    /// token (AdminPassword) embedded in the script must be masked; the non-sensitive token
    /// (Region) must remain visible.
    /// </summary>
    [Test]
    public void ProductBeforeScriptFailure_ScrubArtifacts_MasksSensitiveTokenInArtifact()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "ProductScriptArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = "true";

            try
            {
                RunSchemaQuench();
                _environment.Received(1).Exit(2);
                _progressLog.Received(1).Error("FAILED: " + "Product script quench FAILED");

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "Artifact file must be written even when ScrubArtifacts=true.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Not.Contain("sup3rs3cr3t_product_probe"),
                    "Sensitive product token value must be masked in the artifact when ScrubArtifacts=true.");
                Assert.That(artifactContent, Does.Contain("us-east"),
                    "Non-sensitive token value (Region) must remain visible in the artifact.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
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
                TestHelper.GetTestProductPath("SqlServer", "BaselineValidationArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
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
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// Same BaselineValidationScript failure with ScrubArtifacts=true: the sensitive token value
    /// embedded in the script must be masked; the non-sensitive token (Region) must remain visible.
    /// </summary>
    [Test]
    public void BaselineValidationScriptFailure_ScrubArtifacts_MasksSensitiveTokenInArtifact()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "BaselineValidationArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "Artifact file must be written even when ScrubArtifacts=true.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Not.Contain("sup3rs3cr3t_baseline_probe"),
                    "Sensitive token value must be masked in the BaselineValidationScript artifact when ScrubArtifacts=true.");
                Assert.That(artifactContent, Does.Contain("us-east"),
                    "Non-sensitive token value (Region) must remain visible in the artifact.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
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
                TestHelper.GetTestProductPath("SqlServer", "VersionStampArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
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
                cfg["ScrubArtifacts"] = null;
                cfg["SchemaPackagePath"] = null;
            }
        }
    }

    /// <summary>
    /// Same VersionStampScript failure with ScrubArtifacts=true: the sensitive token value
    /// embedded in the script must be masked; the non-sensitive token (Region) must remain visible.
    /// </summary>
    [Test]
    public void VersionStampScriptFailure_ScrubArtifacts_MasksSensitiveTokenInArtifact()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(null, null);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "VersionStampArtifactProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["ArtifactPath"] = _artifactDir;
            FactoryContainer.Resolve<IConfigurationRoot>()["ScrubArtifacts"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.Received(1).Exit(2);

                var artifactFiles = Directory.GetFiles(_artifactDir, "*.sql");
                Assert.That(artifactFiles, Has.Length.GreaterThan(0),
                    "Artifact file must be written even when ScrubArtifacts=true.");

                var artifactContent = string.Join("\n", artifactFiles.Select(File.ReadAllText));
                Assert.That(artifactContent, Does.Not.Contain("sup3rs3cr3t_stamp_probe"),
                    "Sensitive token value must be masked in the VersionStampScript artifact when ScrubArtifacts=true.");
                Assert.That(artifactContent, Does.Contain("us-east"),
                    "Non-sensitive token value (Region) must remain visible in the artifact.");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
                cfg["ArtifactPath"] = null;
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
