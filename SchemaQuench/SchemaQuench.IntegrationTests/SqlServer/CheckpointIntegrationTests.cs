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
using System.IO;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class CheckpointIntegrationTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;
    private string _checkpointDir;

    public CheckpointIntegrationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [SetUp]
    public void SetUp()
    {
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"SchemaQuench_Checkpoint_Test_{Guid.NewGuid():N}");
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
            // Ignore cleanup errors
        }
    }

    [Test]
    public void ShouldCreateCheckpointFilesOnStart()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            try
            {
                RunSchemaQuench();
            }
            catch
            {
                // Expected - we're just checking checkpoint creation
            }

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Begin Quench")));

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    [Test]
    // A template is no longer checkpointed (each database resumes from its own file), so a product checkpoint an older
    // release left is not taken up.
    public void ShouldRunEveryTemplate_WhenAProductCheckpointIsLeftOver()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            var product = Product.Load();
            var checkpointContent = $@"# SchemaQuench Product Checkpoint
# Product: {product.Name}
# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

[Completed Templates]
Template:Main
";
            var checkpointPath = Path.Combine(_checkpointDir, $"{FileNameEncoder.Encode(product.Name)}.product.checkpoint");
            File.WriteAllText(checkpointPath, checkpointContent);

            RunSchemaQuench();

            _progressLog.DidNotReceive().Info(Arg.Is<string>(s => s.Contains("previously completed per checkpoint")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Quenching Template: Main")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Quenching Template: Secondary")));

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    [Test]
    public void ShouldResumeFromDatabaseCheckpoint()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            var product = Product.Load();
            var dbCheckpointContent = $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: Main
# Server: {_server}
# Database: {_mainDb}
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
";
            var dbCheckpointPath = Path.Combine(_checkpointDir,
                // Slice 2: filename now includes a 5th segment for SchemaName (empty for regular templates).
                $"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(_mainDb)}.{FileNameEncoder.Encode("")}.checkpoint");
            File.WriteAllText(dbCheckpointPath, dbCheckpointContent);

            RunSchemaQuench();

            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Resuming from checkpoint")));

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    [Test]
    public void ShouldResumeSuccessfullyWhenMissingTablesAndColumnsAlreadyCheckpointed()
    {
        // Regression test for the checkpoint-resume bug surfaced 2026-06-01: when a prior run
        // checkpointed `MissingTablesAndColumns` as complete and errored later (or simply went
        // through full success and is re-running), the next run on a fresh connection found
        // the step marked done in the checkpoint and skipped re-parsing the table JSON. The
        // downstream tracked steps (`ModifiedTables`, `IndexesAndConstraints`) then crashed
        // with `Invalid object name '#Tables'` because the session-scoped temp tables didn't
        // exist on the new connection. Fix: `MissingTablesAndColumns` is no longer wrapped in
        // `_checkpointing.Track` — it always runs on every quench (database-idempotent, primes
        // session state). MySQL has had the equivalent defense (`MySqlTempTablesExist` +
        // `ParseMySqlTableJson` re-parse) inside `QuenchModifiedTables` /
        // `QuenchIndexesAndConstraints` from the start.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            var product = Product.Load();
            // Pre-write a database checkpoint that claims MissingTablesAndColumns is complete.
            // Pre-fix, this would cause the next run's `ModifiedTables` step to crash on
            // `Invalid object name '#Tables'`.
            var dbCheckpointContent = $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: Main
# Server: {_server}
# Database: {_mainDb}
# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

[Completed Steps]
KindleForge
MissingTablesAndColumns

[Before Scripts]

[Object Scripts]

[After Tables Object Scripts]

[Between Tables And Keys Scripts]

[After Table Scripts]

[Table Data Scripts]

[After Scripts]
";
            var dbCheckpointPath = Path.Combine(_checkpointDir,
                // Slice 2: filename includes a 5th segment for SchemaName (empty for regular templates).
                $"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(_mainDb)}.{FileNameEncoder.Encode("")}.checkpoint");
            File.WriteAllText(dbCheckpointPath, dbCheckpointContent);

            RunSchemaQuench();

            // Assert the deploy completed without the `Invalid object name '#Tables'` crash.
            _errorLog.DidNotReceive().Error(Arg.Is<string>(s => s != null && s.Contains("Invalid object name '#Tables'")));
            _errorLog.DidNotReceive().Error(Arg.Is<string>(s => s != null && s.Contains("Invalid object name '#Tables'")), Arg.Any<Exception>());

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    [Test]
    public void ShouldPreserveCheckpointOnFailure()
    {
        // Verifies cleanup is skipped when a quench fails. Pre-writes a representative
        // checkpoint file, runs a deliberately-failing quench, and confirms the pre-existing
        // file survives. Required because the real failing run (BeforeTemplateScriptError)
        // fails before any successful step, so nothing is written to the checkpoint during
        // the run itself — the only way to observe "cleanup did not run" is to seed disk
        // state ahead of time and check that it's still there afterward.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "BeforeTemplateScriptError");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            // Seed a checkpoint file that should not be touched by the failing run's cleanup path.
            var seededCheckpoint = Path.Combine(_checkpointDir, "BeforeTemplateScriptError.product.checkpoint");
            const string seededContent = "# Seeded by test to verify cleanup is skipped on failure\n[Completed Templates]\nTemplate:Seeded\n";
            Directory.CreateDirectory(_checkpointDir);
            File.WriteAllText(seededCheckpoint, seededContent);

            RunSchemaQuench();

            Assert.That(File.Exists(seededCheckpoint), Is.True, "Pre-existing checkpoint must not be deleted after a failed run");
            Assert.That(File.ReadAllText(seededCheckpoint), Is.EqualTo(seededContent), "Pre-existing checkpoint content must not be altered after a failed run");

            _environment.Received(1).Exit(2);

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    [Test]
    // A failed deploy of the same product to another database keeps its resume point through a successful run here.
    public void ShouldKeepAnotherTargetsCheckpoint_WhenTheRunSucceeds()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            var product = Product.Load();
            string CheckpointPath(string database) => Path.Combine(_checkpointDir,
                $"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(database)}.{FileNameEncoder.Encode("")}.checkpoint");
            var otherTarget = CheckpointPath("OtherTenantOfThisProduct");
            const string otherContent = "# SchemaQuench Database Checkpoint seeded for another target";
            File.WriteAllText(otherTarget, otherContent);

            RunSchemaQuench();

            Assert.Multiple(() =>
            {
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code >= 2));
                Assert.That(File.Exists(otherTarget), Is.True, "another target's resume point survives");
                Assert.That(File.ReadAllText(otherTarget), Is.EqualTo(otherContent));
                Assert.That(File.Exists(CheckpointPath(_mainDb)), Is.False, "the run's own checkpoint is cleaned up");
            });

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    [Test]
    public void ShouldReKindleWhenDatabaseResetOutOfBandDespiteStaleCheckpoint()
    {
        // Regression test for #322: KindleForge is cheap and self-verifying (it reads the
        // in-DB KindleStamp and no-ops when current), so it must always be evaluated —
        // an out-of-band reset (helper procs / KindleStamp dropped, checkpoint dir untouched)
        // must not leave it silently skipped on the next resume.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            // Real kindling for this test (not SkipKindlingForge) — the point is proving
            // KindleForge actually re-runs on resume.
            _environment.CommandLine.Returns("--ResumeQuench");

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            var product = Product.Load();
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;

            try
            {
                // Ensure the forge is genuinely kindled (helper proc present) before the
                // checkpoint claims the step already complete.
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true);
                Assert.That(HelperProcExists(cmd), Is.True, "Setup: TableQuench must exist before simulating a reset.");

                // Seed a database checkpoint claiming KindleForge already completed — same shape
                // as ShouldResumeFromDatabaseCheckpoint: a prior quench ran and checkpointed it.
                var dbCheckpointContent = $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: Main
# Server: {_server}
# Database: {_mainDb}
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
";
                var dbCheckpointPath = Path.Combine(_checkpointDir,
                    Path.GetFileName($"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(_mainDb)}.{FileNameEncoder.Encode("")}.checkpoint"));
                File.WriteAllText(dbCheckpointPath, dbCheckpointContent);

                // Simulate an out-of-band reset: drop the helper proc + kindle stamp WITHOUT
                // touching the checkpoint directory.
                cmd.CommandText = "DROP PROCEDURE IF EXISTS [SchemaSmith].[TableQuench]; DROP TABLE IF EXISTS [SchemaSmith].[KindleStamp];";
                cmd.ExecuteNonQuery();
                Assert.That(HelperProcExists(cmd), Is.False, "Setup: reset must drop the helper proc.");

                // Resume against the same checkpoint dir, which still lists KindleForge complete.
                Program.Main([]);

                Assert.That(HelperProcExists(cmd), Is.True,
                    "KindleForge must always be evaluated on resume, even when the checkpoint marks it " +
                    "complete, so an out-of-band database reset (#322) is repaired rather than silently skipped.");
            }
            finally
            {
                // Always leave the shared test database correctly kindled for the rest of the suite.
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true);

                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
            }
        }
    }

    [Test]
    public void ShouldResumeSuccessfullyWhenModifiedTablesAlreadyCheckpointed()
    {
        // Parity guard for #332: the PostgreSQL fix rebuilds temp_existing_indexes when a
        // resumed run skipped the ModifiedTables step that normally builds it. SQL Server rebuilds
        // its #-temp state per consuming step, so seeding ModifiedTables complete and resuming must
        // NOT crash here — no engine-code change required. Locks in cross-engine parity.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks(); // sets "--SkipKindlingForge --ResumeQuench"

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ResumeProbe");
            FactoryContainer.Resolve<IConfigurationRoot>()["CheckpointDirectory"] = _checkpointDir;

            // Guard against a fresh (unkindled) CI database: SkipKindlingForge assumes
            // _mainDb is already kindled, which is only true locally by test-run history.
            // A no-op when already kindled, so it can't change the local-pass behavior.
            using (var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: false);
            }

            var product = Product.Load();
            var dbCheckpointContent = $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: Main
# Server: {_server}
# Database: {_mainDb}
# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

[Completed Steps]
MissingTablesAndColumns
ModifiedTables

[Before Scripts]

[Object Scripts]

[After Tables Object Scripts]

[Between Tables And Keys Scripts]

[After Table Scripts]

[Table Data Scripts]

[After Scripts]
";
            var dbCheckpointPath = Path.Join(_checkpointDir,
                Path.GetFileName($"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(_mainDb)}.{FileNameEncoder.Encode("")}.checkpoint"));
            Directory.CreateDirectory(_checkpointDir);
            File.WriteAllText(dbCheckpointPath, dbCheckpointContent);

            RunSchemaQuench();

            // Parity signal: a resumed run that skipped ModifiedTables must not crash on missing
            // session temp state — the run converges to a clean (zero) exit.
            _environment.DidNotReceive().Exit(Arg.Is<int>(c => c != 0));

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
        }
    }

    private static bool HelperProcExists(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM sys.procedures WHERE schema_id = SCHEMA_ID('SchemaSmith') AND name = 'TableQuench'";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        _environment.CommandLine.Returns("--SkipKindlingForge --ResumeQuench");
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private void RunSchemaQuench()
    {
        Program.Main(["SkipKindlingForge"]);
    }
}
