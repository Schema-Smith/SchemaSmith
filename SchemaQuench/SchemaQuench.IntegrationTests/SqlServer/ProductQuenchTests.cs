// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using log4net;
using Newtonsoft.Json.Linq;
using Schema.IntegrationTests;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class ProductQuenchTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _secondaryDb;
    private readonly string _mainDb;
    private readonly string _server;

    public ProductQuenchTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _secondaryDb = config["ScriptTokens:SecondaryDB"];
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [Test]
    public void ShouldQuenchValidProductSuccessfully()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            File.WriteAllText("SchemaQuench - Quench Tables XXX.sql", "This File To Be Deleted");
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            var product = Product.Load();

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @$"
TRUNCATE TABLE SchemaSmith.CompletedMigrationScripts
TRUNCATE TABLE SchemaSmith.TestLog
-- Slice 2 contract: writes always populate template_name with the actual template the row
-- belongs to. The prune DELETE is strict on template_name = @template so each template's
-- prune only deletes its own rows (legacy blank-template rows persist as harmless residue).
INSERT SchemaSmith.CompletedMigrationScripts ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name]) VALUES('MigrationScripts/Before/MigrationScript0.sql', '{product.Name}', 'Before', 'Main', '')
INSERT SchemaSmith.CompletedMigrationScripts ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name]) VALUES('MigrationScripts/Before/Obsolete.sql', '{product.Name}', 'Before', 'Main', '') -- this entry should be removed from the CompletedMigrationScripts table
";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = @$"
TRUNCATE TABLE SchemaSmith.CompletedMigrationScripts
TRUNCATE TABLE SchemaSmith.TestLog";
            cmd.ExecuteNonQuery();
            conn.Close();

            RunSchemaQuench();

            _progressLog.DidNotReceive().Error(Arg.Any<string>());
            _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] Successfully Quenched");
            _progressLog.Received(1).Info($"[{_server}].[{_secondaryDb}] Successfully Quenched");
            _progressLog.Received(1).Info("Completed quench of ValidProduct");

            _progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Quenching After Product Scripts to")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.EndsWith($"Jobs{Path.DirectorySeparatorChar}Job 1.sql")));
            _progressLog.Received(1).Info(Arg.Is<string>(s => s.EndsWith($"Jobs{Path.DirectorySeparatorChar}SubFolder{Path.DirectorySeparatorChar}Job 2.sql")));
            _progressLog.Received(3).Warn(Arg.Any<string>());
            _progressLog.Received(1).Warn(Arg.Is<string>(s => s.EndsWith("Unresolved token: {{Unresolved}}")));
            _progressLog.Received(2).Warn(Arg.Is<string>(s => s.EndsWith("Unresolved token: {{Other}}")));

            _environment.DidNotReceive().Exit(2);
            _environment.DidNotReceive().Exit(3);

            AssertScriptsQuenched(_mainDb);
            AssertScriptsQuenched(_secondaryDb);

            AssertCompletedMigrationsMarked(_mainDb, ExpectedMainCompletedMigrations);
            AssertCompletedMigrationsMarked(_secondaryDb, ExpectedSecondaryCompletedMigrations);

            AssertTableCreatedWithExtendedProperties(_mainDb, "dbo.TestTable");
            AssertTableCreatedWithExtendedProperties(_secondaryDb, "dbo.TestSecondaryTable");

            conn.Open();
            conn.ChangeDatabase(_mainDb);
            cmd.CommandText = "SELECT COUNT(*) FROM dbo.TestTable WITH (NOLOCK)";
            Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(5));
            conn.Close();

            // Verify indexed view was created
            using var verifyIvConn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            verifyIvConn.Open();
            verifyIvConn.ChangeDatabase(_mainDb);
            using var verifyIvCmd = verifyIvConn.CreateCommand();
            verifyIvCmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'dbo' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
            Assert.That((int)verifyIvCmd.ExecuteScalar()!, Is.EqualTo(1), "Indexed view should exist after quench");
            verifyIvConn.Close();

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldWriteDeploymentSummaryOnSuccessfulQuench()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            var product = Product.Load();

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @$"
TRUNCATE TABLE SchemaSmith.CompletedMigrationScripts
TRUNCATE TABLE SchemaSmith.TestLog
INSERT SchemaSmith.CompletedMigrationScripts ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name]) VALUES('MigrationScripts/Before/MigrationScript0.sql', '{product.Name}', 'Before', 'Main', '')
";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = @$"
TRUNCATE TABLE SchemaSmith.CompletedMigrationScripts
TRUNCATE TABLE SchemaSmith.TestLog";
            cmd.ExecuteNonQuery();
            conn.Close();

            RunSchemaQuench();

            _progressLog.DidNotReceive().Error(Arg.Any<string>());
            _environment.DidNotReceive().Exit(2);
            _environment.DidNotReceive().Exit(3);

            var logDir = ConfigHelper.ResolveLogPath();
            var jsonPath = Path.Join(logDir, "SchemaQuench - Summary.json");
            var mdPath = Path.Join(logDir, "SchemaQuench - Summary.md");

            Assert.That(File.Exists(jsonPath), Is.True, $"Expected deployment summary JSON at {jsonPath}");
            Assert.That(File.Exists(mdPath), Is.True, $"Expected deployment summary Markdown at {mdPath}");

            var json = JObject.Parse(File.ReadAllText(jsonPath));
            Assert.That(json.SelectToken("run.outcome")?.Value<string>(), Is.EqualTo("Success"));
            Assert.That(json.SelectToken("run.mode")?.Value<string>(), Is.EqualTo("Quench"));
            // A fresh (non-resumed) run must report resumedFromCheckpoint=false — regression guard
            // for reading the checkpoint at emit time, which would see this run's own completions.
            Assert.That(json.SelectToken("run.resumedFromCheckpoint")?.Value<bool>(), Is.False);
            var targets = (JArray)json.SelectToken("targets");
            Assert.That(targets, Is.Not.Null.And.Not.Empty);

            // #243 E5: SQL Server object-change audit is wired, so a real run is instrumented and the
            // object scripts (procedures/views/functions) that re-apply are counted as "ran".
            Assert.That(json.SelectToken("objectChanges.instrumented")?.Value<bool>(), Is.True,
                "objectChanges should be instrumented once the audit reader drains the session table");
            Assert.That(json.SelectToken("objectChanges.scriptsRan")?.Value<int>(), Is.GreaterThan(0),
                "object scripts that ran should be counted");

            var markdown = File.ReadAllText(mdPath);
            Assert.That(markdown, Is.Not.Empty);

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldWhatIfValidProductWithoutQuenchingAnything()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            config["WhatIfONLY"] = "true";

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @$"
TRUNCATE TABLE SchemaSmith.CompletedMigrationScripts
TRUNCATE TABLE SchemaSmith.TestLog
INSERT SchemaSmith.CompletedMigrationScripts ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name]) VALUES('MigrationScripts/Before/MigrationScript0.sql', 'ValidProduct', 'Before', 'Main', '')
";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = @$"
TRUNCATE TABLE SchemaSmith.CompletedMigrationScripts
TRUNCATE TABLE SchemaSmith.TestLog";
            cmd.ExecuteNonQuery();
            conn.Close();

            // Capture database state before WhatIf run
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts";
            var mainMigrationCountBefore = (int)cmd.ExecuteScalar();
            cmd.CommandText = "IF OBJECT_ID('dbo.TestTable') IS NULL SELECT -1 ELSE EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.TestTable WITH (NOLOCK)'";
            var mainTestTableRowsBefore = (int)cmd.ExecuteScalar();
            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts";
            var secondaryMigrationCountBefore = (int)cmd.ExecuteScalar();
            conn.Close();

            try
            {
                RunSchemaQuench();

                // No errors should occur
                _progressLog.DidNotReceive().Error(Arg.Any<string>());
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                // Should complete successfully
                _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] Successfully Quenched");
                _progressLog.Received(1).Info($"[{_server}].[{_secondaryDb}] Successfully Quenched");
                _progressLog.Received(1).Info("Completed quench of ValidProduct");

                // WhatIf log messages for Main template database quench
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Object scripts without unresolved tokens:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Object scripts without query tokens:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Before database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Object scripts (after tables):")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Between table and keys scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] After table scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Object scripts (final pass):")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Table data delivery:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] After database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("[WhatIf] Would stamp version")));

                // WhatIf log messages for Secondary template database quench
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb) && s.Contains("[WhatIf] Object scripts without unresolved tokens:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb) && s.Contains("[WhatIf] Before database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb) && s.Contains("[WhatIf] After database scripts:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_secondaryDb) && s.Contains("[WhatIf] Would stamp version")));

                // WhatIf "Would APPLY" and "Would SKIP" messages for object scripts
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("MySchema.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("dbo.MyFunction.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("dbo.MyView.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("dbo.MyProcedure.sql")));

                // Before migration scripts: MigrationScript0 was previously quenched, MigrationScript1 [ALWAYS] should be Would APPLY
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would SKIP (previously quenched):") && s.Contains("MigrationScript0.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("MigrationScript1 [ALWAYS].sql")));

                // Table data delivery WhatIf for Main (TestTable has ContentFile and MergeType)
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would DELIVER:") && s.Contains("TestTable")));

                // After Product scripts should show "Would Quench"
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("[master]") && s.Contains("Would Quench") && s.Contains("Job 1.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("[master]") && s.Contains("Would Quench") && s.Contains("Job 2.sql")));

                // Verify nothing was actually quenched - TestLog should be empty (truncated before run)
                var mainScriptLog = GetScriptLog(_mainDb, "TestLog", "Msg", "Id");
                Assert.That(mainScriptLog, Is.Empty, "No scripts should have been quenched in MainDB TestLog");

                var secondaryScriptLog = GetScriptLog(_secondaryDb, "TestLog", "Msg", "Id");
                Assert.That(secondaryScriptLog, Is.Empty, "No scripts should have been quenched in SecondaryDB TestLog");

                // Verify database state was not modified by WhatIf
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts";
                Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(mainMigrationCountBefore), "MainDB CompletedMigrationScripts should be unchanged");

                cmd.CommandText = "IF OBJECT_ID('dbo.TestTable') IS NULL SELECT -1 ELSE EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.TestTable WITH (NOLOCK)'";
                Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(mainTestTableRowsBefore), "MainDB TestTable row count should be unchanged");

                conn.ChangeDatabase(_secondaryDb);
                cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts";
                Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(secondaryMigrationCountBefore), "SecondaryDB CompletedMigrationScripts should be unchanged");
                conn.Close();
            }
            finally
            {
                config["WhatIfONLY"] = "false";
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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "TemplateObjectsScriptError");

            RunSchemaQuench();

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("Unable to quench all scripts")));
            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("One or more database quenches FAILED")));
            _environment.Received(1).Exit(2);
            _environment.DidNotReceive().Exit(3);

            // #338 refinement: the Failures.log roll-up (mirrored in Summary.json failures[]) surfaces
            // the SPECIFIC per-script error + artifact for a user-script failure — not the generic
            // "Unable to quench all scripts" / n/a.
            var json = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
            var failure = json.SelectToken("failures[0]");
            Assert.That(failure, Is.Not.Null, "a script failure should be recorded");
            Assert.That(failure!.SelectToken("error")?.Value<string>(),
                Does.StartWith("Unable to quench '").And.Not.EqualTo("Unable to quench all scripts"),
                "Error line should name the specific failed script + its error");
            Assert.That(failure.SelectToken("artifactPath")?.Value<string>(), Does.Contain("Failed"),
                "Debug SQL should point at the Failed <script> artifact, not n/a");

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldExitWithReturnCodeWhenBeforeTemplateScripErrors()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "BeforeTemplateScriptError");

            RunSchemaQuench();

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("KABOOM!") && !s.Contains("Unable to quench")));
            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("KABOOM!") && s.Contains("Unable to quench")));
            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Resolved SQL written to:") && s.Contains("MigrationScripts")));
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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "BadVersionStamp");

            RunSchemaQuench();

            _progressLog.Received(2).Error(Arg.Is<string>(s => s.EndsWith("BAD STAMP!")));
            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("FAILED to quench:\r\nBAD STAMP!")));
            _environment.Received(1).Exit(2);
            _environment.DidNotReceive().Exit(3);

            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
    }

    [Test]
    public void ShouldFailWhenAfterProductScripErrors()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "AfterProductScriptError");

            RunSchemaQuench();
            _environment.Received(1).Exit(2);
            _progressLog.Received(1).Error("FAILED: " + "Product script quench FAILED");

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Unable to quench") && s.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("Jobs/Job 1.sql") && s.Contains("KABOOM")));

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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "InvalidServer");

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
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private void RunSchemaQuench()
    {
        Program.Main(["SkipKindlingForge"]);
    }

    private void AssertTableCreatedWithExtendedProperties(string dbName, string tableName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT CAST(CASE WHEN OBJECT_ID('{tableName}') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @$"
SELECT CONVERT(VARCHAR(50), x.[value]) AS [value]
  FROM fn_listextendedproperty(default, 'Schema', 'dbo', 'Table', default, default, default) x
  WHERE objname COLLATE DATABASE_DEFAULT = '{tableName.Split(['.'])[1]}'
    AND x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'
";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("ValidProduct"));
        conn.Close();
    }

    private static readonly List<string> ExpectedScriptLog =
    [
        @"Before\MigrationScript1.sql",
        "MySchema.sql",
        "Type1.sql",
        "Type2.sql",
        "MyCatalog.sql",
        "MyStoplist.sql",
        "dbo.MyFunction.sql",
        "dbo.MyView.sql",
        "dbo.MyProcedure.sql",
        "dbo.MyTrigger.sql",
        "dbo.FunctionThatNeedsView.sql", // this one will error on the first attempt and gets run again (only in Main)
        @"After\MigrationScript1.sql"
    ];

    private void AssertScriptsQuenched(string dbName)
    {
        var scriptLog = GetScriptLog(dbName, "TestLog", "Msg", "Id");

        var expected = ExpectedScriptLog.Where(l => dbName.Contains("Main") || !l.Equals("dbo.FunctionThatNeedsView.sql")).ToList();
        Assert.That(expected, Is.EquivalentTo(scriptLog)); // Validate all expected scripts are quenched in the expected order
    }

    private static readonly List<string> ExpectedMainCompletedMigrations =
    [
        "MigrationScripts/After/MigrationScript1.sql",
        "MigrationScripts/After/Populate TestTable.sql",
        "MigrationScripts/Before/MigrationScript0.sql"
    ];

    private static readonly List<string> ExpectedSecondaryCompletedMigrations =
    [
        "MigrationScripts/After/MigrationScript1.sql",
        "MigrationScripts/Before/MigrationScript1.sql"
    ];

    private void AssertCompletedMigrationsMarked(string dbName, List<string> expected)
    {
        var scriptLog = GetScriptLog(dbName, "CompletedMigrationScripts", "ScriptPath", "ScriptPath");
        Assert.That(scriptLog, Is.EquivalentTo(expected)); // Validate all expected run once migration scripts are marked as run
    }

    private List<string> GetScriptLog(string dbName, string logTable, string msgCol, string orderCol)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [{msgCol}] FROM [{dbName}].SchemaSmith.{logTable} WITH (NOLOCK) ORDER BY {orderCol}";
        using var reader = cmd.ExecuteReader();
        var scriptLog = new List<string>();
        while (reader.Read()) scriptLog.Add(reader[msgCol].ToString());
        conn.Close();

        return scriptLog;
    }
}
