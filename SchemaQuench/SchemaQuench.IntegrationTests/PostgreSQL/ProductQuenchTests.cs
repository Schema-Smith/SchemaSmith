// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using log4net;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
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
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
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
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "ValidProduct");
            var product = Product.Load();

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @$"
TRUNCATE TABLE ""SchemaSmith"".""CompletedMigrationScripts"";
DROP TABLE IF EXISTS ""SchemaSmith"".""TestLog"";
-- Slice 2 contract: writes always populate template_name with the actual template the row
-- belongs to. The prune DELETE is strict on template_name = @template so each template's
-- prune only deletes its own rows (legacy blank-template rows persist as harmless residue).
INSERT INTO ""SchemaSmith"".""CompletedMigrationScripts"" (""ScriptPath"", ""ProductName"", ""QuenchSlot"", template_name, schema_name) VALUES('After Scripts/Create TestSecondary.sql', '{product.Name}', 'After', 'Main', '');
INSERT INTO ""SchemaSmith"".""CompletedMigrationScripts"" (""ScriptPath"", ""ProductName"", ""QuenchSlot"", template_name, schema_name) VALUES('Before Scripts/MigrationScript0.sql', '{product.Name}', 'Before', 'Main', '');
INSERT INTO ""SchemaSmith"".""CompletedMigrationScripts"" (""ScriptPath"", ""ProductName"", ""QuenchSlot"", template_name, schema_name) VALUES('Before Scripts/Obsolete.sql', '{product.Name}', 'Before', 'Main', ''); -- this entry should be removed from the CompletedMigrationScripts table
";
            cmd.ExecuteNonQuery();

            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = @"
TRUNCATE TABLE ""SchemaSmith"".""CompletedMigrationScripts"";
DROP TABLE IF EXISTS ""SchemaSmith"".""TestLog"";
";
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

            AssertTableCreatedWithProductOwnership(_mainDb, "public", "TestTable");
            AssertTableCreatedWithProductOwnership(_secondaryDb, "public", "TestSecondaryTable");

            conn.Open();
            conn.ChangeDatabase(_mainDb);
            cmd.CommandText = "SELECT COUNT(*) FROM public.\"TestTable\"";
            Assert.That(cmd.ExecuteScalar() as long?, Is.EqualTo(5));
            conn.Close();

            // Verify materialized view was created
            using var verifyMvConn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            verifyMvConn.Open();
            verifyMvConn.ChangeDatabase(_mainDb);
            using var verifyMvCmd = verifyMvConn.CreateCommand();
            verifyMvCmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'testsummaryview'";
            Assert.That((long)verifyMvCmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should exist after quench");
            verifyMvConn.Close();

            // #243 E5: PostgreSQL object-change audit is wired, so a real run is instrumented and the
            // object scripts (functions/views) that re-apply are counted as "ran".
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
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "ValidProduct");
            config["WhatIfONLY"] = "true";

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @$"
TRUNCATE TABLE ""SchemaSmith"".""CompletedMigrationScripts"";
DROP TABLE IF EXISTS ""SchemaSmith"".""TestLog"";
INSERT INTO ""SchemaSmith"".""CompletedMigrationScripts"" (""ScriptPath"", ""ProductName"", ""QuenchSlot"", template_name, schema_name) VALUES('Before Scripts/MigrationScript0.sql', 'ValidProduct', 'Before', 'Main', '');
";
            cmd.ExecuteNonQuery();

            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = @"
TRUNCATE TABLE ""SchemaSmith"".""CompletedMigrationScripts"";
DROP TABLE IF EXISTS ""SchemaSmith"".""TestLog"";
";
            cmd.ExecuteNonQuery();
            conn.Close();

            // Capture database state before WhatIf run
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""CompletedMigrationScripts""";
            var mainMigrationCountBefore = (long)cmd.ExecuteScalar();
            cmd.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'TestTable') THEN (SELECT COUNT(*) FROM public.\"TestTable\") ELSE -1 END";
            var mainTestTableRowsBefore = (long)cmd.ExecuteScalar();
            conn.ChangeDatabase(_secondaryDb);
            cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""CompletedMigrationScripts""";
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

                // WhatIf "Would APPLY" messages for object scripts
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("MySchema.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("public.MyFunction.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("public.MyView.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("public.MyProcedure.sql")));

                // Before migration scripts: MigrationScript0 was previously quenched, MigrationScript1 [ALWAYS] should be Would APPLY
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would SKIP (previously quenched):") && s.Contains("MigrationScript0.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would APPLY:") && s.Contains("MigrationScript1 [ALWAYS].sql")));

                // Table data delivery WhatIf for Main (TestTable has ContentFile and MergeType)
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains(_mainDb) && s.Contains("Would DELIVER:") && s.Contains("TestTable")));

                // After Product scripts should show "Would Quench"
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("[postgres]") && s.Contains("Would Quench") && s.Contains("Job 1.sql")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("[postgres]") && s.Contains("Would Quench") && s.Contains("Job 2.sql")));

                // Verify nothing was actually quenched - TestLog should not exist (we dropped it before run)
                // (In WhatIf mode, the 00-Initialize DB Objects.sql that creates TestLog is not executed)

                // Verify database state was not modified by WhatIf
                conn.Open();
                conn.ChangeDatabase(_mainDb);
                cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""CompletedMigrationScripts""";
                Assert.That((long)cmd.ExecuteScalar(), Is.EqualTo(mainMigrationCountBefore), "MainDB CompletedMigrationScripts should be unchanged");

                cmd.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'TestTable') THEN (SELECT COUNT(*) FROM public.\"TestTable\") ELSE -1 END";
                Assert.That((long)cmd.ExecuteScalar(), Is.EqualTo(mainTestTableRowsBefore), "MainDB TestTable row count should be unchanged");

                conn.ChangeDatabase(_secondaryDb);
                cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""CompletedMigrationScripts""";
                Assert.That((long)cmd.ExecuteScalar(), Is.EqualTo(secondaryMigrationCountBefore), "SecondaryDB CompletedMigrationScripts should be unchanged");
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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "TemplateObjectsScriptError");

            RunSchemaQuench();

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("Unable to quench all scripts")));
            _progressLog.Received(1).Error(Arg.Is<string>(s => s.EndsWith("One or more database quenches FAILED")));
            _environment.Received(1).Exit(2);
            _environment.DidNotReceive().Exit(3);

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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "BeforeTemplateScriptError");

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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "BadVersionStamp");

            RunSchemaQuench();

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("BAD STAMP!")));
            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("FAILED to quench:") && s.EndsWith("BAD STAMP!")));
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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "AfterProductScriptError");

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

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "InvalidServer");

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

    private void AssertTableCreatedWithProductOwnership(string dbName, string tableSchema, string tableName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = '{tableSchema}' AND table_name = '{tableName}')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @$"SELECT ""ProductName"" FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""Schema"" = '{tableSchema}' AND ""TableName"" = '{tableName}' AND ""IndexName"" IS NULL;";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("ValidProduct"));
        conn.Close();
    }

    private static readonly List<string> ExpectedScriptLog =
    [
        @"Before\MigrationScript1.sql",
        "MySchema.sql",
        "Type1.sql",
        "Type2.sql",
        "public.MyFunction.sql",
        "public.MyView.sql",
        "public.MyProcedure.sql",
        "public.MyTrigger.sql",
        "public.FunctionThatNeedsView.sql", // this one will error on the first attempt and gets run again (only in Main)
        @"After\MigrationScript1.sql"
    ];

    private void AssertScriptsQuenched(string dbName)
    {
        var scriptLog = GetScriptLog(dbName, "TestLog", "Msg", "Id");

        var expected = ExpectedScriptLog.Where(l => dbName.Contains("Main") || !l.Equals("public.FunctionThatNeedsView.sql")).ToList();
        Assert.That(expected, Is.EquivalentTo(scriptLog)); // Validate all expected scripts are quenched in the expected order
    }

    private static readonly List<string> ExpectedMainCompletedMigrations =
    [
        "Before Scripts/00-Initialize DB Objects.sql",
        "After Scripts/Create TestSecondary.sql",
        "After Scripts/MigrationScript1.sql",
        "After Scripts/Populate TestTable.sql",
        "Before Scripts/MigrationScript0.sql"
    ];

    private static readonly List<string> ExpectedSecondaryCompletedMigrations =
    [
        "Before Scripts/00-Initialize DB Objects.sql",
        "After Scripts/MigrationScript1.sql",
        "Before Scripts/MigrationScript1.sql"
    ];

    private void AssertCompletedMigrationsMarked(string dbName, List<string> expected)
    {
        var scriptLog = GetScriptLog(dbName, "CompletedMigrationScripts", "ScriptPath", "ScriptPath");
        Assert.That(scriptLog, Is.EquivalentTo(expected)); // Validate all expected run once migration scripts are marked as run
    }

    private List<string> GetScriptLog(string dbName, string logTable, string msgCol, string orderCol)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{msgCol}\" FROM \"SchemaSmith\".\"{logTable}\" ORDER BY \"{orderCol}\"";
        using var reader = cmd.ExecuteReader();
        var scriptLog = new List<string>();
        while (reader.Read()) scriptLog.Add(reader[msgCol].ToString());
        conn.Close();

        return scriptLog;
    }
}
