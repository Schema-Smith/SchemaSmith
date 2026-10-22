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
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests verifying the per-script "should not apply" sentinel skip on MySQL.
/// Three scenarios per the feature design: run-once migration sentinel-skips and is recorded as
/// completed (no re-run), object script sentinel-skips without failing the run, and a
/// non-sentinel error still fails the run (control).
/// </summary>
[Category("Integration")]
public abstract class SentinelSkipIntegrationTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract IConfigurationRoot FixtureConfig { get; }
    protected abstract string ProductPlatformFolder { get; }

    private const string ProductName = "SentinelSkipProduct";

    private ILog _errorLog = null!;
    private ILog _progressLog = null!;
    private IEnvironment _environment = null!;
    private string _mainDb = null!;
    private string _adminConnectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _errorLog = Substitute.For<ILog>();
        _progressLog = Substitute.For<ILog>();
        _environment = Substitute.For<IEnvironment>();

        _mainDb = MainDb;
        _adminConnectionString = BaseConnectionString + "Database=information_schema;";
    }

    /// <summary>
    /// A run-once migration that raises the sentinel is treated as success AND recorded as
    /// completed in SchemaSmith_CompletedMigrationScripts. A second quench does NOT re-execute
    /// it — the marker-table row count stays at 1.
    /// </summary>
    [Test]
    public void RunOnceMigration_SentinelSkip_IsRecordedAsCompleted_AndNotReRunOnSecondQuench()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            CleanupSentinelState();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);

            try
            {
                // Quench 1: migration script runs, raises sentinel, should be recorded as done.
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Skipped (ShouldNotApply)") && s.Contains("SentinelMigration.sql")));

                // Tracking row must exist — sentinel skip is a success, so it's recorded.
                Assert.That(CountCompletedMigrationRows("MigrationScripts/Before/SentinelMigration.sql"), Is.EqualTo(1L),
                    "SentinelMigration.sql must be recorded in SchemaSmith_CompletedMigrationScripts after sentinel skip.");

                // The deployment summary reports what happened to it: skipped, not "Ran".
                var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
                var sentinel = ((JArray)summary["migrationScripts"]!).FirstOrDefault(m => (m["path"]?.Value<string>() ?? "").EndsWith("SentinelMigration.sql"));
                Assert.That(sentinel?["outcome"]?.Value<string>() ?? "not listed", Is.EqualTo("Skipped"), summary["migrationScripts"]!.ToString());

                // Pre-sentinel INSERT committed before the sentinel was raised.
                Assert.That(CountMarkerRows(), Is.EqualTo(1L),
                    "INSERT before the sentinel must have committed on the first quench.");

                // Quench 2: migration is already recorded, must be skipped entirely.
                _progressLog.ClearReceivedCalls();
                _environment.ClearReceivedCalls();
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // Marker row count must not increase — the migration did not re-execute.
                Assert.That(CountMarkerRows(), Is.EqualTo(1L),
                    "Marker row count must not increase on second quench — migration must not re-execute.");
            }
            finally
            {
                CleanupSentinelState();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// An object script (stored procedure) that raises the sentinel is skipped and the run
    /// still succeeds — no exit(2), "Skipped (ShouldNotApply)" logged.
    /// </summary>
    [Test]
    public void ObjectScript_SentinelSkip_DoesNotFailRun()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            CleanupSentinelState();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);

            try
            {
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Skipped (ShouldNotApply)") && s.Contains("SentinelProc.sql")));
            }
            finally
            {
                CleanupSentinelState();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Control: a script raising a non-sentinel error still fails the run. Sentinel skip must
    /// not turn all errors into skips.
    /// </summary>
    [Test]
    public void NonSentinelError_StillFailsRun()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath(ProductPlatformFolder, "BeforeTemplateScriptError");

            RunSchemaQuench();

            _environment.Received(1).Exit(2);
            _progressLog.Received().Error(Arg.Is<string>(s => s.Contains("KABOOM!")));

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    // ----- Helpers -------------------------------------------------------------------------------

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();

        FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private void CleanupSentinelState()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SET @drop_marker := (SELECT IF(COUNT(*) > 0,
    'DROP TABLE IF EXISTS `{_mainDb}`.`SentinelMarker`',
    'SELECT 1')
    FROM information_schema.tables
    WHERE table_schema = '{_mainDb}' AND table_name = 'SentinelMarker');
PREPARE stmt FROM @drop_marker; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @del_tracking := (SELECT IF(COUNT(*) > 0,
    'DELETE FROM `{_mainDb}`.`SchemaSmith_CompletedMigrationScripts` WHERE `ProductName` = ''{ProductName}''',
    'SELECT 1')
    FROM information_schema.tables
    WHERE table_schema = '{_mainDb}' AND table_name = 'SchemaSmith_CompletedMigrationScripts');
PREPARE stmt FROM @del_tracking; EXECUTE stmt; DEALLOCATE PREPARE stmt;";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private long CountCompletedMigrationRows(string scriptPath)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.`SchemaSmith_CompletedMigrationScripts` WHERE `ProductName` = '{ProductName}' AND `ScriptPath` = '{scriptPath}'";
        var result = Convert.ToInt64(cmd.ExecuteScalar());
        conn.Close();
        return result;
    }

    private long CountMarkerRows()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.`SentinelMarker`";
        var result = Convert.ToInt64(cmd.ExecuteScalar());
        conn.Close();
        return result;
    }
}
