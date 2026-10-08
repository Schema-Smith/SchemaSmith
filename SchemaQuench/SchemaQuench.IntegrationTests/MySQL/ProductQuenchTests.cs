// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.IntegrationTests.MySQL;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[TestFixture]
public class ProductQuenchTests : ProductQuenchTestsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string FixtureProductFolder => "TestProduct";
}

/// <summary>
/// Integration tests for ProductQuench error scenarios.
/// Tests that the product quench properly handles and reports various error conditions.
/// Uses test products from the TestProducts folder.
/// </summary>
[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class ProductUpdateTests
{
    private ILog _errorLog = null!;
    private ILog _progressLog = null!;
    private IEnvironment _environment = null!;
    private string _connectionString = null!;
    private string _secondaryDb = null!;
    private string _mainDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Ensure FixtureSetup has run to initialize the test databases
        FixtureSetup.EnsureInitialized();

        _errorLog = Substitute.For<ILog>();
        _progressLog = Substitute.For<ILog>();
        _environment = Substitute.For<IEnvironment>();

        // Use config from FixtureSetup, matching SQL Server/PostgreSQL pattern
        _connectionString = FixtureSetup.ConnectionString + "Database=information_schema;";
        _mainDb = FixtureSetup.MainDb;
        _secondaryDb = FixtureSetup.SecondaryDb;
    }

    [Test]
    public void ShouldQuenchValidProductSuccessfully()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            // Delete any old quench script files
            foreach (var file in Directory.GetFiles(".", "SchemaQuench - Quench Tables*.sql"))
                File.Delete(file);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "ValidProduct");
            var product = Product.Load();

            // Setup test infrastructure in databases
            using var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(_connectionString);
            conn.Open();
            SetupTestLogTable(conn, _mainDb);
            SetupTestLogTable(conn, _secondaryDb);
            SetupCompletedMigrationScripts(conn, _mainDb, product.Name);
            SetupCompletedMigrationScripts(conn, _secondaryDb, product.Name);
            conn.Close();

            RunSchemaQuench();

            _progressLog.DidNotReceive().Error(Arg.Any<string>());
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Successfully Quenched")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains(_secondaryDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Successfully Quenched")));
            _progressLog.Received(1).Info("Completed quench of ValidProduct");

            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Quenching After Product Scripts to")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Job 1.sql")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Job 2.sql")));

            // Check for unresolved token warnings
            _progressLog.Received().Warn(Arg.Is<string>(s => s.Contains("Unresolved token")));

            _environment.DidNotReceive().Exit(2);
            _environment.DidNotReceive().Exit(3);

            AssertScriptsQuenched(_mainDb);
            AssertScriptsQuenched(_secondaryDb);

            // #243 E5: MySQL object-change audit is wired, so a real run is instrumented and the
            // object scripts (procedures/views/functions) that re-apply are counted as "ran".
            var summaryJson = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
            Assert.That(summaryJson.SelectToken("objectChanges.instrumented")?.Value<bool>(), Is.True,
                "objectChanges should be instrumented once the audit reader drains the session table");
            Assert.That(summaryJson.SelectToken("objectChanges.scriptsRan")?.Value<int>(), Is.GreaterThan(0),
                "object scripts that ran should be counted");

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldWhatIfValidProductWithoutQuenchingAnything()
    {
        // The WhatIf output includes "Would DELIVER" only where data delivery is supported (MySQL 8.0+);
        // MySQL 5.7 gates delivery, so that line is absent — skip this assertion below the floor.
        using (var vconn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(_connectionString))
        {
            vconn.Open();
            using var vcmd = vconn.CreateCommand();
            vcmd.CommandText = "SELECT VERSION()";
            var parts = (vcmd.ExecuteScalar()?.ToString() ?? "").Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var mj) && int.TryParse(parts[1], out var mn) && mj * 100 + mn < 800)
                Assert.Ignore("Data delivery requires MySQL 8.0; 'Would DELIVER' is absent below the floor.");
        }

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "ValidProduct");
            config["WhatIfOnly"] = "true";

            using var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(_connectionString);
            conn.Open();
            SetupTestLogTable(conn, _mainDb);
            SetupTestLogTable(conn, _secondaryDb);
            SetupCompletedMigrationScripts(conn, _mainDb, "ValidProduct");
            SetupCompletedMigrationScripts(conn, _secondaryDb, "ValidProduct");

            // Capture database state before WhatIf run
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.`SchemaSmith_CompletedMigrationScripts`";
            var mainMigrationCountBefore = (long)cmd.ExecuteScalar();
            cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.`SchemaSmith_TestLog`";
            var mainTestLogCountBefore = (long)cmd.ExecuteScalar();
            cmd.CommandText = $"SELECT COUNT(*) FROM `{_secondaryDb}`.`SchemaSmith_CompletedMigrationScripts`";
            var secondaryMigrationCountBefore = (long)cmd.ExecuteScalar();
            conn.Close();

            try
            {
                RunSchemaQuench();

                // No errors should occur
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // Should complete successfully
                _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Successfully Quenched")));
                _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains(_secondaryDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Successfully Quenched")));
                _progressLog.Received(1).Info("Completed quench of ValidProduct");

                // WhatIf log messages for Main template database quench
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Object scripts without unresolved tokens:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Object scripts without query tokens:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Before database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Object scripts (after tables):")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Between table and keys scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] After table scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Object scripts (final pass):")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Table data delivery:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] After database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Would stamp version")));

                // WhatIf log messages for Secondary template database quench
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Object scripts without unresolved tokens:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Before database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] After database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb, StringComparison.OrdinalIgnoreCase) && s.Contains("[WhatIf] Would stamp version")));

                // WhatIf "Would APPLY" messages for object scripts
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Would APPLY:") && s.Contains("MyFunction.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Would APPLY:") && s.Contains("MyView.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Would APPLY:") && s.Contains("MyProcedure.sql")));

                // Before migration scripts: MigrationScript0 was previously quenched, MigrationScript1 [ALWAYS] should be Would APPLY
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Would SKIP (previously quenched):") && s.Contains("MigrationScript0.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Would APPLY:") && s.Contains("MigrationScript1 [ALWAYS].sql")));

                // Table data delivery WhatIf for Main (TestTable has ContentFile and MergeType)
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb, StringComparison.OrdinalIgnoreCase) && s.Contains("Would DELIVER:") && s.Contains("TestTable")));

                // After Product scripts should show "Would Quench"
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Would Quench") && s.Contains("Job 1.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Would Quench") && s.Contains("Job 2.sql")));

                // Verify nothing was actually quenched - TestLog should be empty (truncated before run)
                conn.Open();
                var mainScriptLog = GetScriptLog(_mainDb, "SchemaSmith_TestLog", "Msg", "Id");
                Assert.That(mainScriptLog, Is.Empty, "No scripts should have been quenched in MainDB TestLog");

                var secondaryScriptLog = GetScriptLog(_secondaryDb, "SchemaSmith_TestLog", "Msg", "Id");
                Assert.That(secondaryScriptLog, Is.Empty, "No scripts should have been quenched in SecondaryDB TestLog");

                // Verify database state was not modified by WhatIf
                cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.`SchemaSmith_CompletedMigrationScripts`";
                Assert.That((long)cmd.ExecuteScalar(), Is.EqualTo(mainMigrationCountBefore), "MainDB CompletedMigrationScripts should be unchanged");

                cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.`SchemaSmith_TestLog`";
                Assert.That((long)cmd.ExecuteScalar(), Is.EqualTo(mainTestLogCountBefore), "MainDB TestLog should be unchanged");

                cmd.CommandText = $"SELECT COUNT(*) FROM `{_secondaryDb}`.`SchemaSmith_CompletedMigrationScripts`";
                Assert.That((long)cmd.ExecuteScalar(), Is.EqualTo(secondaryMigrationCountBefore), "SecondaryDB CompletedMigrationScripts should be unchanged");
                conn.Close();
            }
            finally
            {
                config["WhatIfOnly"] = "false";
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void ShouldErrorOnObjectsScriptThatCannotBeQuenchedWithRetry()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "TemplateObjectsScriptError");

            RunSchemaQuench();

            _progressLog.Received().Error(Arg.Is<string>(s => s.Contains("Unable to quench")));
            _environment.Received(1).Exit(2);
            _environment.DidNotReceive().Exit(3);

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldExitWithReturnCodeWhenBeforeTemplateScriptErrors()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "BeforeTemplateScriptError");

            RunSchemaQuench();

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("KABOOM!")));
            _environment.Received(1).Exit(2);
            _environment.DidNotReceive().Exit(3);

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldExitWithReturnCodeWhenVersionStampErrors()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "BadVersionStamp");

            RunSchemaQuench();

            _progressLog.Received().Error(Arg.Is<string>(s => s.Contains("BAD STAMP!")));
            _environment.Received(1).Exit(2);
            _environment.DidNotReceive().Exit(3);

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldFailWhenAfterProductScriptErrors()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "AfterProductScriptError");

            RunSchemaQuench();
            _environment.Received(1).Exit(2);
            _progressLog.Received(1).Error("FAILED: " + "Product script quench FAILED");

            _progressLog.Received().Error(Arg.Is<string>(s => s.Contains("Unable to quench") && s.Contains("Job 1.sql") && s.Contains("KABOOM")));

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldFailWhenInvalidServer()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("MySQL", "InvalidServer");

            RunSchemaQuench();
            _environment.Received(1).Exit(2);
            _progressLog.Received(1).Error("FAILED: " + "Invalid server for this product");

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();

        // Re-register the config captured by FixtureSetup (other tests may have cleared it).
        // This config already has Target:* and ScriptTokens:* keys set by
        // Schema.IntegrationTests.MySQL.FixtureSetup, matching the SQL Server/PostgreSQL pattern.
        FactoryContainer.Register(FixtureSetup.Config);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private void RunSchemaQuench()
    {
        Program.Main(["SkipKindlingForge"]);
    }

    private void SetupTestLogTable(IDbConnection conn, string dbName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS `{dbName}`.`SchemaSmith_TestLog` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `Msg` VARCHAR(500) NOT NULL
            );
            TRUNCATE TABLE `{dbName}`.`SchemaSmith_TestLog`;
        ";
        cmd.ExecuteNonQuery();
    }

    private void SetupCompletedMigrationScripts(IDbConnection conn, string dbName, string productName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS `{dbName}`.`SchemaSmith_CompletedMigrationScripts` (
                `ScriptPath` VARCHAR(500) NOT NULL,
                `ProductName` VARCHAR(100) NOT NULL,
                `QuenchSlot` VARCHAR(50) NOT NULL,
                `template_name` VARCHAR(255) NOT NULL DEFAULT '',
                `schema_name` VARCHAR(255) NOT NULL DEFAULT '',
                PRIMARY KEY (`ScriptPath`, `ProductName`)
            );
            TRUNCATE TABLE `{dbName}`.`SchemaSmith_CompletedMigrationScripts`;
            INSERT INTO `{dbName}`.`SchemaSmith_CompletedMigrationScripts` (`ScriptPath`, `ProductName`, `QuenchSlot`, `template_name`, `schema_name`)
            VALUES ('MigrationScripts/Before/MigrationScript0.sql', '{productName}', 'Before', 'Main', '');
        ";
        cmd.ExecuteNonQuery();

        // Also clear ProductOwnership rows for this product so a real quench in this test
        // starts from a clean ownership slate. Same shape as the PG SchemaTemplateHappyPathTests
        // DropTenantSchemas fix — a real quench writes ownership rows; without per-test cleanup
        // a sibling fixture quenching a shared table can hit "tables already owned by another
        // product". Guarded with IF EXISTS in case a test runs before ForgeKindler has fully set
        // the table up (defensive — production order has ForgeKindler in FixtureSetup).
        cmd.CommandText = $@"
            SET @ddl := (SELECT IF(COUNT(*) > 0,
                'DELETE FROM `{dbName}`.`SchemaSmith_ProductOwnership` WHERE `ProductName` = ''{productName}''',
                'SELECT 1')
                FROM information_schema.tables
                WHERE table_schema = '{dbName}' AND table_name = 'SchemaSmith_ProductOwnership');
            PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;
        ";
        cmd.ExecuteNonQuery();
    }

    private static readonly List<string> ExpectedScriptLog =
    [
        "Before/MigrationScript1.sql",
        "MyFunction.sql",
        "MyView.sql",
        "MyProcedure.sql",
        "FunctionThatNeedsView.sql",
        "MyTrigger.sql",
        "After/MigrationScript1.sql"
    ];

    private void AssertScriptsQuenched(string dbName)
    {
        var scriptLog = GetScriptLog(dbName, "SchemaSmith_TestLog", "Msg", "Id");

        // Filter expected scripts based on whether it's Main or Secondary db
        var expected = ExpectedScriptLog.Where(l =>
            dbName.Contains("Main") || !l.Equals("FunctionThatNeedsView.sql")).ToList();

        // Verify expected scripts are quenched
        foreach (var expectedScript in expected)
        {
            Assert.That(scriptLog.Any(s => s.Contains(expectedScript.Replace("/", "\\")) || s.Contains(expectedScript)),
                Is.True, $"Expected script '{expectedScript}' to be quenched in {dbName}");
        }
    }

    private List<string> GetScriptLog(string dbName, string logTable, string msgCol, string orderCol)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT `{msgCol}` FROM `{dbName}`.`{logTable}` ORDER BY `{orderCol}`";
        using var reader = cmd.ExecuteReader();
        var scriptLog = new List<string>();
        while (reader.Read()) scriptLog.Add(reader[msgCol]?.ToString() ?? "");
        conn.Close();

        return scriptLog;
    }
}
