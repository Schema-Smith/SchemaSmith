// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.Reporting;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// CheckpointManager-style unit/integration tests that exercise FileCheckpointManager's
/// on-disk persistence directly — file naming (FileNameEncoder), section layout, and
/// delete semantics. Uses a temp checkpoint directory per test.
/// </summary>
[Category("Integration")]
public abstract class CheckpointIntegrationTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract IConfigurationRoot FixtureConfig { get; }
    protected abstract string ProductPlatformFolder { get; }

    private ILog _errorLog = null!;
    private ILog _progressLog = null!;
    private IEnvironment _environment = null!;
    private string _checkpointDir = null!;
    private string _connectionString = null!;
    private string _mainDb = null!;
    private string _server = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _errorLog = Substitute.For<ILog>();
        _progressLog = Substitute.For<ILog>();
        _environment = Substitute.For<IEnvironment>();

        _connectionString = BaseConnectionString + "Database=information_schema;";
        _mainDb = MainDb;
        _server = FixtureConfig["Target:Server"] ?? "localhost";
    }

    [SetUp]
    public void SetUp()
    {
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"SchemaQuench_Checkpoint_Test_{Guid.NewGuid():N}");

        _errorLog.ClearReceivedCalls();
        _progressLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
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
    public void CheckpointManager_ShouldCreateDirectory()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var nestedDir = Path.Combine(_checkpointDir, "nested", "checkpoint");

            var manager = new FileCheckpointManager(nestedDir);

            Assert.That(Directory.Exists(nestedDir), Is.True);
            Assert.That(manager, Is.Not.Null);
        }
    }

    [Test]
    public void CheckpointManager_ShouldSaveAndLoadProductCheckpoint()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var manager = new FileCheckpointManager(_checkpointDir);
            var scope = new TrackingScope { ProductName = "TestProduct" };

            manager.MarkScriptCompleted(scope, "Before", "Scripts/Before1.sql");
            manager.MarkStepCompleted(scope, "Template:Main");
            manager.MarkScriptCompleted(scope, "After", "Scripts/After1.sql");

            var filePath = Path.Combine(_checkpointDir, "TestProduct.product.checkpoint");
            Assert.That(File.Exists(filePath), Is.True, "Product checkpoint file should exist on disk");

            var content = File.ReadAllText(filePath);
            Assert.That(content, Does.Contain("# Product: TestProduct"));
            Assert.That(content, Does.Contain("[Before Product Scripts - default]"));
            Assert.That(content, Does.Contain("Scripts/Before1.sql"));
            Assert.That(content, Does.Contain("[Completed Templates]"));
            Assert.That(content, Does.Contain("Template:Main"));
            Assert.That(content, Does.Contain("[After Product Scripts - default]"));
            Assert.That(content, Does.Contain("Scripts/After1.sql"));

            Assert.That(manager.HasCompleted(scope, "Template:Main"), Is.True);
            Assert.That(manager.HasCompletedScript(scope, "Before", "Scripts/Before1.sql"), Is.True);
            Assert.That(manager.HasCompletedScript(scope, "After", "Scripts/After1.sql"), Is.True);
        }
    }

    [Test]
    public void CheckpointManager_ShouldSaveAndLoadDatabaseCheckpoint()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var manager = new FileCheckpointManager(_checkpointDir);
            var scope = new TrackingScope
            {
                ProductName = "TestProduct",
                TemplateName = "Main",
                Server = "localhost",
                DatabaseName = "TestDB"
            };

            manager.MarkStepCompleted(scope, "KindleForge");
            manager.MarkStepCompleted(scope, "ValidateBaseline");
            manager.MarkScriptCompleted(scope, "Before", "MigrationScripts/Script1.sql");
            manager.MarkScriptCompleted(scope, "Object", "Functions/Func1.sql");

            // Slice 2: filename includes a 5th segment for SchemaName (empty for regular templates).
            var filePath = Path.Combine(_checkpointDir, "TestProduct.Main.localhost.TestDB..checkpoint");
            Assert.That(File.Exists(filePath), Is.True, "Database checkpoint file should exist on disk");

            var content = File.ReadAllText(filePath);
            Assert.That(content, Does.Contain("# Product: TestProduct"));
            Assert.That(content, Does.Contain("# Template: Main"));
            Assert.That(content, Does.Contain("# Server: localhost"));
            Assert.That(content, Does.Contain("# Database: TestDB"));
            Assert.That(content, Does.Contain("[Completed Steps]"));
            Assert.That(content, Does.Contain("KindleForge"));
            Assert.That(content, Does.Contain("ValidateBaseline"));
            Assert.That(content, Does.Contain("[Before Scripts]"));
            Assert.That(content, Does.Contain("MigrationScripts/Script1.sql"));
            Assert.That(content, Does.Contain("[Object Scripts]"));
            Assert.That(content, Does.Contain("Functions/Func1.sql"));

            Assert.That(manager.HasCompleted(scope, "KindleForge"), Is.True);
            Assert.That(manager.HasCompletedScript(scope, "Before", "MigrationScripts/Script1.sql"), Is.True);
        }
    }

    [Test]
    public void CheckpointManager_ShouldDeleteProductCheckpoint()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var manager = new FileCheckpointManager(_checkpointDir);
            var scope = new TrackingScope { ProductName = "TestProduct" };
            manager.MarkStepCompleted(scope, "Template:Main");

            var filePath = Path.Combine(_checkpointDir, "TestProduct.product.checkpoint");
            Assert.That(File.Exists(filePath), Is.True);

            manager.DeleteCheckpoints("TestProduct");

            Assert.That(File.Exists(filePath), Is.False, "Product checkpoint file should be deleted");
        }
    }

    [Test]
    public void CheckpointManager_ShouldDeleteDatabaseCheckpoints()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var manager = new FileCheckpointManager(_checkpointDir);

            var scope1 = new TrackingScope { ProductName = "TestProduct", TemplateName = "Main", Server = "localhost", DatabaseName = "DB1" };
            var scope2 = new TrackingScope { ProductName = "TestProduct", TemplateName = "Main", Server = "localhost", DatabaseName = "DB2" };
            manager.MarkStepCompleted(scope1, "KindleForge");
            manager.MarkStepCompleted(scope2, "KindleForge");

            // Slice 2: filename includes a 5th segment for SchemaName (empty for regular templates).
            var path1 = Path.Combine(_checkpointDir, "TestProduct.Main.localhost.DB1..checkpoint");
            var path2 = Path.Combine(_checkpointDir, "TestProduct.Main.localhost.DB2..checkpoint");
            Assert.That(File.Exists(path1), Is.True);
            Assert.That(File.Exists(path2), Is.True);

            manager.DeleteCheckpoints("TestProduct");

            Assert.That(File.Exists(path1), Is.False, "DB1 checkpoint should be deleted");
            Assert.That(File.Exists(path2), Is.False, "DB2 checkpoint should be deleted");
        }
    }

    [Test]
    public void CheckpointManager_ShouldSanitizeServerName()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var manager = new FileCheckpointManager(_checkpointDir);
            var scope = new TrackingScope
            {
                ProductName = "TestProduct",
                TemplateName = "Main",
                Server = "server:3306/instance",
                DatabaseName = "TestDB"
            };

            manager.MarkStepCompleted(scope, "KindleForge");

            var files = Directory.GetFiles(_checkpointDir, "*.checkpoint");
            Assert.That(files.Length, Is.EqualTo(1));

            var fileName = Path.GetFileName(files[0]);
            Assert.That(fileName, Does.Contain("server%3A3306%2Finstance"),
                "Server name with illegal filename characters should be percent-encoded");
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
            FactoryContainer.Register(FixtureConfig);
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            // Real kindling for this test (not SkipKindlingForge) — the point is proving
            // KindleForge actually re-runs on resume.
            _environment.CommandLine.Returns("--ResumeQuench");

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ValidProduct");
            config["CheckpointDirectory"] = _checkpointDir;
            Directory.CreateDirectory(_checkpointDir);

            var product = Product.Load();
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;

            try
            {
                // Ensure the forge is genuinely kindled (helper proc present) before the
                // checkpoint claims the step already complete.
                ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: true);
                Assert.That(HelperProcExists(cmd), Is.True, "Setup: SchemaSmith_TableQuench must exist before simulating a reset.");

                // Seed a database checkpoint claiming KindleForge already completed — same shape
                // as SqlServer/PostgreSQL's ShouldResumeFromDatabaseCheckpoint: a prior quench ran
                // and checkpointed it.
                var dbCheckpointContent = $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: Main
# Server: {_server}
# Database: {StoredDatabaseName()}
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
                    Path.GetFileName($"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(StoredDatabaseName())}.{FileNameEncoder.Encode("")}.checkpoint"));
                File.WriteAllText(dbCheckpointPath, dbCheckpointContent);

                // Simulate an out-of-band reset: drop the helper proc + kindle stamp WITHOUT
                // touching the checkpoint directory.
                cmd.CommandText = "DROP PROCEDURE IF EXISTS SchemaSmith_TableQuench";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "DROP TABLE IF EXISTS SchemaSmith_KindleStamp";
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
                ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: true);

                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<ICheckpointing>();
            }
        }
    }

    [Test]
    public void ShouldResumeSuccessfullyWhenModifiedTablesAlreadyCheckpointed()
    {
        // Parity guard for #332: the PostgreSQL fix rebuilds temp_existing_indexes when a
        // resumed run skipped the ModifiedTables step that normally builds it. MySQL re-parses its
        // session temp tables via MySqlTempTablesExist + ParseMySqlTableJson inside the consuming
        // steps, so seeding ModifiedTables complete and resuming must NOT crash here — no engine-code
        // change required. Locks in cross-engine parity.
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(FixtureConfig);
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            _environment.CommandLine.Returns("--SkipKindlingForge --ResumeQuench");

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ResumeProbe");
            config["CheckpointDirectory"] = _checkpointDir;
            Directory.CreateDirectory(_checkpointDir);

            // Guard against a fresh (unkindled) CI database: SkipKindlingForge assumes
            // _mainDb is already kindled, which is only true locally by test-run history.
            // A no-op when already kindled, so it can't change the local-pass behavior.
            using (var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: false);
            }

            var product = Product.Load();
            // The deploy keys its checkpoint by the name the server reports, which is lowercase under
            // lower_case_table_names=1; on a case-sensitive file system the declared spelling is a different file.
            var storedDb = StoredDatabaseName();
            var dbCheckpointContent = $@"# SchemaQuench Database Checkpoint
# Product: {product.Name}
# Template: Main
# Server: {_server}
# Database: {storedDb}
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
                Path.GetFileName($"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(storedDb)}.{FileNameEncoder.Encode("")}.checkpoint"));
            File.WriteAllText(dbCheckpointPath, dbCheckpointContent);

            try
            {
                Program.Main(["SkipKindlingForge"]);

                // Parity signal: a resumed run that skipped ModifiedTables must not crash on missing
                // session temp state — the run converges to a clean (zero) exit.
                _environment.DidNotReceive().Exit(Arg.Is<int>(c => c != 0));

                // And its summary says it resumed: the training certification saw false on a run where every unit resumed.
                var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
                Assert.That(summary.SelectToken("run.resumedFromCheckpoint")?.Value<bool>(), Is.True);

                // 1.4 run identity: the summary names the run the progress log opened with and the package content it
                // deployed; the seeded checkpoint predates 2.9, so it names no earlier run.
                var runId = (string)summary.SelectToken("run.id");
                Assert.That(runId, Is.Not.Null.And.Not.Empty, "run.id");
                _progressLog.Received(1).Info(Arg.Is<object>(o => o.ToString() == $"Run {runId}"));
                var expected = PackageContentHash.ForFolder(Path.GetDirectoryName(product.FilePath), ConfigHelper.ResolveLogPath());
                Assert.That((string)summary.SelectToken("run.package.contentHash"), Is.EqualTo(expected.ContentHash));
                Assert.That((int?)summary.SelectToken("run.package.fileCount"), Is.EqualTo(expected.FileCount));
                Assert.That(summary.SelectToken("run.resumesRunId")?.Type, Is.EqualTo(JTokenType.Null));
                Assert.That((string)summary.SelectToken("run.host.machine"), Is.EqualTo(Environment.MachineName));
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<ICheckpointing>();
            }
        }
    }

    // A16: a preview loaded the checkpoint (which, without --ResumeQuench, discards it) and a successful preview then
    // cleaned up the run's checkpoints, so "deploy fails, preview, resume" started the resume over from scratch.
    [Test]
    public void APreviewLeavesAFailedDeploysResumePointForTheResume()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(FixtureConfig);
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ResumeProbe");
            config["CheckpointDirectory"] = _checkpointDir;
            Directory.CreateDirectory(_checkpointDir);
            using (var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString))
            {
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                using var cmd = conn.CreateCommand();
                ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: false);
            }

            var product = Product.Load();
            var storedDb = StoredDatabaseName();
            var dbCheckpointPath = Path.Join(_checkpointDir,
                Path.GetFileName($"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(storedDb)}.{FileNameEncoder.Encode("")}.checkpoint"));
            File.WriteAllText(dbCheckpointPath, $"# SchemaQuench Database Checkpoint\n# Product: {product.Name}\n# Template: Main\n# Server: {_server}\n# Database: {storedDb}\n\n[Completed Steps]\nModifiedTables\n");

            try
            {
                _environment.CommandLine.Returns("--SkipKindlingForge --WhatIf");
                Program.Main(["SkipKindlingForge", "WhatIf"]);
                Assert.That(File.Exists(dbCheckpointPath), Is.True, "the failed deploy's resume point survived the preview");

                _environment.ClearReceivedCalls();
                _environment.CommandLine.Returns("--SkipKindlingForge --ResumeQuench");
                Program.Main(["SkipKindlingForge"]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(c => c != 0));
                var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
                Assert.That(summary.SelectToken("run.resumedFromCheckpoint")?.Value<bool>(), Is.True, "the resume took it up");
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<ICheckpointing>();
            }
        }
    }

    // The MySQL family's twins of the SQL Server and PostgreSQL resume tests: a seeded database checkpoint is resumed
    // from, and a failed run leaves the checkpoints it found.
    private void RegisterRun(string product)
    {
        FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
        _environment.CommandLine.Returns("--SkipKindlingForge --ResumeQuench");
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, product);
        config["CheckpointDirectory"] = _checkpointDir;
        Directory.CreateDirectory(_checkpointDir);
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: false);
    }

    [Test]
    public void ShouldResumeFromDatabaseCheckpoint()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            RegisterRun("ValidProduct");
            var product = Product.Load();
            var storedDb = StoredDatabaseName();
            File.WriteAllText(Path.Join(_checkpointDir,
                    Path.GetFileName($"{FileNameEncoder.Encode(product.Name)}.{FileNameEncoder.Encode("Main")}.{FileNameEncoder.Encode(_server)}.{FileNameEncoder.Encode(storedDb)}.{FileNameEncoder.Encode("")}.checkpoint")),
                $"# SchemaQuench Database Checkpoint\n# Product: {product.Name}\n# Template: Main\n# Server: {_server}\n# Database: {storedDb}\n" +
                $"# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n[Completed Steps]\nKindleForge\n\n[Before Scripts]\n\n[Object Scripts]\n\n" +
                "[After Tables Object Scripts]\n\n[Between Tables And Keys Scripts]\n\n[After Table Scripts]\n\n[Table Data Scripts]\n\n[After Scripts]\n");
            try
            {
                Program.Main(["SkipKindlingForge"]);

                _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains(storedDb) && s.Contains("Resuming from checkpoint")));
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<ICheckpointing>();
            }
        }
    }

    [Test]
    public void ShouldPreserveCheckpointOnFailure()
    {
        // The failing run stops before any step succeeds, so nothing it writes shows that cleanup was skipped: a checkpoint
        // seeded ahead of it has to still be there, unchanged.
        lock (FactoryContainer.SharedLockObject)
        {
            RegisterRun("BeforeTemplateScriptError");
            var seeded = Path.Join(_checkpointDir, "BeforeTemplateScriptError.product.checkpoint");
            const string seededContent = "# Seeded by test to verify cleanup is skipped on failure\n[Completed Templates]\nTemplate:Seeded\n";
            File.WriteAllText(seeded, seededContent);
            try
            {
                Program.Main(["SkipKindlingForge"]);

                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(seeded), Is.True, "a failed run must not delete a checkpoint");
                    Assert.That(File.ReadAllText(seeded), Is.EqualTo(seededContent), "nor change it");
                    _environment.Received(1).Exit(2);
                });
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<ICheckpointing>();
            }
        }
    }

    private string StoredDatabaseName()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{_mainDb}'";
        return cmd.ExecuteScalar()?.ToString() ?? _mainDb;
    }

    private static bool HelperProcExists(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.routines " +
                           $"WHERE {MySqlNameMatch.FoldedRoutineSchema("routine_schema", "DATABASE()")} AND routine_name = 'SchemaSmith_TableQuench'";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }
}
