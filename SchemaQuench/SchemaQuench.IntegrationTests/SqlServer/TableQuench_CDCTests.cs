// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[NonParallelizable]
public class TableQuench_CDCTests : BaseTableQuenchTests
{
    // sp_cdc_enable_table writes replication metadata (sp_MSverboselogging -> SetReplicated), which is
    // database-wide -- so it deadlocks against any sibling fixture doing DDL in the same database. This
    // fixture being NonParallelizable does not prevent that: NUnit still runs parallelizable fixtures
    // alongside the non-parallel queue. Observed once as a 1205 wrapped inside error 22832, which is why
    // matching on the deadlock text matters -- the outer error number is not 1205.
    //
    // Retrying beats serializing the whole suite, and beats narrowing the run to a mode that happens to
    // pass. The re-check exists because a failed enable can leave the table already enabled.
    private void ExecuteWithDeadlockRetry(IDbCommand cmd)
    {
        var sql = cmd.CommandText;
        // A deadlock rolls back only the statement it hits, so re-running the whole batch repeats DDL that already
        // succeeded and fails with "There is already an object named ...". Everything before the CDC enable runs once;
        // only the enable onward is retried.
        var enableAt = sql.IndexOf("EXEC sys.sp_cdc_enable_table", StringComparison.OrdinalIgnoreCase);
        if (enableAt > 0 && sql[..enableAt].Trim().Length > 0)
        {
            cmd.CommandText = sql[..enableAt];
            cmd.ExecuteNonQuery();
            sql = sql[enableAt..];
            cmd.CommandText = sql;
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                cmd.ExecuteNonQuery();
                return;
            }
            catch (DbException e) when (e.Message.ContainsIgnoringCase("deadlock victim"))
            {
                Thread.Sleep(1000);
                cmd.CommandText = sql;
            }
        }

        // Out of retries -- run it once more unguarded so the real error surfaces rather than a
        // synthesized one that hides which command failed.
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void ShouldEnableCDCOnTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create a table without CDC
        cmd.CommandText = "CREATE TABLE dbo.EnableCDCTest (Id INT NOT NULL, Val NVARCHAR(100) NULL)";
        cmd.ExecuteNonQuery();

        // Quench with EnableCDC = true
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[EnableCDCTest]",
                "EnableCDC": true,
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(100)"}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        // Assert CDC is enabled
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.EnableCDCTest')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true));

        // Cleanup: disable CDC before dropping
        cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'EnableCDCTest', @capture_instance = N'dbo_EnableCDCTest'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE dbo.EnableCDCTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void ShouldDisableCDCOnTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create a table and enable CDC on it
        cmd.CommandText = @"
CREATE TABLE dbo.DisableCDCTest (Id INT NOT NULL, Val NVARCHAR(100) NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'DisableCDCTest', @role_name = NULL
";
        ExecuteWithDeadlockRetry(cmd);

        // Verify CDC is enabled before quench
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.DisableCDCTest')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "CDC should be enabled before quench");

        // Quench with EnableCDC = false (default)
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[DisableCDCTest]",
                "EnableCDC": false,
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(100)"}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        // Assert CDC is disabled
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.DisableCDCTest')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(false));

        cmd.CommandText = "DROP TABLE dbo.DisableCDCTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void ShouldRefreshCDCAfterColumnSwap()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with IDENTITY column and enable CDC
        cmd.CommandText = @"
CREATE TABLE dbo.CDCSwapTest (Id INT IDENTITY(1,1) NOT NULL, Val NVARCHAR(100) NULL)
INSERT dbo.CDCSwapTest (Val) VALUES ('before swap')
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCSwapTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);

        // Verify CDC is tracking and capture the original column count
        cmd.CommandText = "SELECT COUNT(*) FROM cdc.captured_columns cc JOIN cdc.change_tables ct ON cc.object_id = ct.object_id WHERE ct.source_object_id = OBJECT_ID('dbo.CDCSwapTest')";
        var columnsBefore = (int)cmd.ExecuteScalar();
        Assert.That(columnsBefore, Is.EqualTo(2), "Should capture both columns initially");

        // Quench: remove IDENTITY (triggers MustSwapColumn swap pattern)
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[CDCSwapTest]",
                "EnableCDC": true,
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        // Assert CDC is still enabled after the swap
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.CDCSwapTest')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "CDC should be re-enabled after column swap");

        // Assert the capture instance has correct column count (fresh instance, not stale)
        // Rotation leaves BOTH instances in place, so scope this to the one this deploy created --
        // an unscoped count sums the old and new column sets and means nothing.
        cmd.CommandText = @"SELECT COUNT(*) FROM cdc.captured_columns cc
                            WHERE cc.object_id = (SELECT TOP 1 ct.object_id FROM cdc.change_tables ct
                                                   WHERE ct.source_object_id = OBJECT_ID('dbo.CDCSwapTest')
                                                   ORDER BY ct.create_date DESC, ct.object_id DESC)";
        var columnsAfter = (int)cmd.ExecuteScalar();
        Assert.That(columnsAfter, Is.EqualTo(2), "Refreshed capture instance should track both columns");

        // Verify data was preserved through the swap
        cmd.CommandText = "SELECT Val FROM dbo.CDCSwapTest";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("before swap"));

        // Cleanup
        cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CDCSwapTest', @capture_instance = N'dbo_CDCSwapTest'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE dbo.CDCSwapTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void ShouldRefreshCDCAfterAddColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table and enable CDC
        cmd.CommandText = @"
CREATE TABLE dbo.CDCAddColTest (Id INT NOT NULL, Val NVARCHAR(100) NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCAddColTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);

        // Verify initial captured column count
        cmd.CommandText = "SELECT COUNT(*) FROM cdc.captured_columns cc JOIN cdc.change_tables ct ON cc.object_id = ct.object_id WHERE ct.source_object_id = OBJECT_ID('dbo.CDCAddColTest')";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(2), "Should capture 2 columns initially");

        // Quench: add a new column
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[CDCAddColTest]",
                "EnableCDC": true,
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true},
                    {"Name": "[NewCol]", "DataType": "INT", "Nullable": true}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        // Assert CDC is still enabled
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.CDCAddColTest')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "CDC should remain enabled");

        // Assert the capture instance now tracks 3 columns (including the new one)
        // Rotation leaves BOTH instances in place, so scope this to the one this deploy created --
        // an unscoped count sums the old and new column sets and means nothing.
        cmd.CommandText = @"SELECT COUNT(*) FROM cdc.captured_columns cc
                            WHERE cc.object_id = (SELECT TOP 1 ct.object_id FROM cdc.change_tables ct
                                                   WHERE ct.source_object_id = OBJECT_ID('dbo.CDCAddColTest')
                                                   ORDER BY ct.create_date DESC, ct.object_id DESC)";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(3), "Refreshed capture instance should track all 3 columns");

        // Cleanup
        cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CDCAddColTest', @capture_instance = N'dbo_CDCAddColTest'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE dbo.CDCAddColTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void DroppingAColumn_DoesNotRotate_AndCaptureContinues()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        try
        {
            // sp_cdc_enable_table is deadlock-prone against the CDC scan job; ExecuteWithDeadlockRetry runs the CREATE
            // once and retries only the enable. The drop clears a table a killed earlier run left behind.
            DropCdcTestTable(cmd);

            cmd.CommandText = @"
CREATE TABLE dbo.CDCDropColTest (Id INT NOT NULL, Val NVARCHAR(100) NULL, Extra INT NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCDropColTest', @role_name = NULL";
            ExecuteWithDeadlockRetry(cmd);

            // Verify initial captured column count
            cmd.CommandText = "SELECT COUNT(*) FROM cdc.captured_columns cc JOIN cdc.change_tables ct ON cc.object_id = ct.object_id WHERE ct.source_object_id = OBJECT_ID('dbo.CDCDropColTest')";
            Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(3), "Should capture 3 columns initially");

            // Quench: remove the Extra column
            var json = """
                {
                    "Schema": "[dbo]",
                    "Name": "[CDCDropColTest]",
                    "EnableCDC": true,
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true}
                    ]
                }
                """;
            RunTableQuenchProc(cmd, json);

            // #509: a dropped column stays in the change table and captures NULL, so the one instance keeps capturing;
            // rotating for it spent the table's second slot. (This asserted the rotated instance's column count before.)
            cmd.CommandText = "INSERT dbo.CDCDropColTest (Id, Val) VALUES (2, 'after the drop')";
            cmd.ExecuteNonQuery();
            WaitForCapture(cmd, "dbo_CDCDropColTest_CT", "Id = 2");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.CDCDropColTest')";
            var sourceColumns = (int)cmd.ExecuteScalar();
            cmd.CommandText = "SELECT Val + '|' + ISNULL(CONVERT(VARCHAR(10), Extra), 'NULL') FROM cdc.dbo_CDCDropColTest_CT WHERE Id = 2";
            var captured = (string)cmd.ExecuteScalar();
            var instances = InstanceCount(cmd, "CDCDropColTest");
            Assert.Multiple(() =>
            {
                Assert.That(sourceColumns, Is.EqualTo(2), "the column is dropped");
                Assert.That(instances, Is.EqualTo(1), "without a rotation");
                Assert.That(captured, Is.EqualTo("after the drop|NULL"), "and the instance keeps capturing, with the dropped column NULL");
            });
        }
        finally
        {
            // In a finally, because a failed assertion above otherwise leaks the table AND its capture
            // instances into every later test in this fixture.
            DropCdcTestTable(cmd);
        }
    }

    /// <summary>
    /// Removes dbo.CDCDropColTest and EVERY capture instance on it.
    /// <para>Every instance, not just <c>dbo_CDCDropColTest</c>: a run that failed part-way through an
    /// earlier shape of this test could leave a rotated instance beside the original, so naming a single
    /// instance leaves the other behind — and SQL Server permits only two per table, so the leftover
    /// eventually makes <c>sp_cdc_enable_table</c> fail for reasons that have nothing to do with the test
    /// that trips over it.</para>
    /// </summary>
    private static void DropCdcTestTable(IDbCommand cmd)
    {
        cmd.CommandText = @"
IF OBJECT_ID('dbo.CDCDropColTest') IS NOT NULL
BEGIN
    DECLARE @ci SYSNAME
    DECLARE ci_cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT ct.capture_instance FROM cdc.change_tables ct
         WHERE ct.source_object_id = OBJECT_ID('dbo.CDCDropColTest')
    OPEN ci_cur
    FETCH NEXT FROM ci_cur INTO @ci
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CDCDropColTest', @capture_instance = @ci
        FETCH NEXT FROM ci_cur INTO @ci
    END
    CLOSE ci_cur
    DEALLOCATE ci_cur
    DROP TABLE dbo.CDCDropColTest
END";
        cmd.ExecuteNonQuery();
    }

    // #420: a capture instance must carry every column of the declared table, computed ones included -- the same
    // shape sp_cdc_enable_table gives on the finished table. Enabling CDC before the computed columns existed left
    // them out for good, because a later deploy sees no column change and never rotates.
    [Test]
    public void ANewTableWithAComputedColumn_CapturesEveryColumn_AndKeepsDoingSo()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        DropWithEveryCaptureInstance(cmd, "CdcComputed");
        try
        {
            var json = """
                {
                    "Schema": "dbo",
                    "Name": "CdcComputed",
                    "EnableCDC": true,
                    "Columns": [
                        { "Name": "Id",    "DataType": "INT", "Nullable": false },
                        { "Name": "A",     "DataType": "INT", "Nullable": true },
                        { "Name": "Twice", "DataType": "INT", "Nullable": true, "ComputedExpression": "[A] * 2" }
                    ],
                    "Indexes": [ { "Name": "PK_CdcComputed", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "Id" } ]
                }
                """;
            RunTableQuenchProc(cmd, json);
            Assert.That(NewestInstanceColumns(cmd, "CdcComputed"), Is.EqualTo(new[] { "A", "Id", "Twice" }), "first deploy");

            RunTableQuenchProc(cmd, json);
            Assert.That(NewestInstanceColumns(cmd, "CdcComputed"), Is.EqualTo(new[] { "A", "Id", "Twice" }), "redeploy");
        }
        finally { DropWithEveryCaptureInstance(cmd, "CdcComputed"); }
    }

    // The rotation for a column change runs in the same place, so a deploy that ADDS a computed column must rotate to
    // an instance that includes it.
    [Test]
    public void AddingAComputedColumnToACapturedTable_RotatesToAnInstanceThatIncludesIt()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        DropWithEveryCaptureInstance(cmd, "CdcAddComputed");
        try
        {
            cmd.CommandText = @"
CREATE TABLE dbo.CdcAddComputed (Id INT NOT NULL PRIMARY KEY, A INT NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcAddComputed', @role_name = NULL";
            ExecuteWithDeadlockRetry(cmd);

            RunTableQuenchProc(cmd, """
                {
                    "Schema": "dbo",
                    "Name": "CdcAddComputed",
                    "EnableCDC": true,
                    "Columns": [
                        { "Name": "Id",    "DataType": "INT", "Nullable": false },
                        { "Name": "A",     "DataType": "INT", "Nullable": true },
                        { "Name": "Twice", "DataType": "INT", "Nullable": true, "ComputedExpression": "[A] * 2" }
                    ]
                }
                """);

            Assert.That(NewestInstanceColumns(cmd, "CdcAddComputed"), Is.EqualTo(new[] { "A", "Id", "Twice" }));
        }
        finally { DropWithEveryCaptureInstance(cmd, "CdcAddComputed"); }
    }

    private static string[] NewestInstanceColumns(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT cc.column_name FROM cdc.captured_columns cc
                             WHERE cc.object_id = (SELECT TOP 1 ct.object_id FROM cdc.change_tables ct
                                                    WHERE ct.source_object_id = OBJECT_ID('dbo.{table}')
                                                    ORDER BY ct.create_date DESC, ct.object_id DESC)
                             ORDER BY cc.column_name";
        using var reader = cmd.ExecuteReader();
        var names = new System.Collections.Generic.List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static void DropWithEveryCaptureInstance(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL
BEGIN
    DECLARE @ci SYSNAME
    DECLARE ci_cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT ct.capture_instance FROM cdc.change_tables ct WHERE ct.source_object_id = OBJECT_ID('dbo.{table}')
    OPEN ci_cur
    FETCH NEXT FROM ci_cur INTO @ci
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'{table}', @capture_instance = @ci
        FETCH NEXT FROM ci_cur INTO @ci
    END
    CLOSE ci_cur
    DEALLOCATE ci_cur
    DROP TABLE dbo.{table}
END";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void ShouldNotRefreshCDCWhenNoColumnChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table, enable CDC, and note the capture instance create time
        cmd.CommandText = @"
CREATE TABLE dbo.CDCNoChangeTest (Id INT NOT NULL, Val NVARCHAR(100) NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCNoChangeTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);

        cmd.CommandText = "SELECT create_date FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.CDCNoChangeTest')";
        var createDateBefore = (System.DateTime)cmd.ExecuteScalar();

        // Small delay to ensure time difference would be detectable
        System.Threading.Thread.Sleep(1100);

        // Quench with identical columns (no changes) but add an index
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[CDCNoChangeTest]",
                "EnableCDC": true,
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true}
                ],
                "Indexes": [
                    {"Name": "[IX_CDCNoChangeTest_Val]", "IndexColumns": "[Val]"}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        // Assert capture instance was NOT recreated (same create_date)
        cmd.CommandText = "SELECT create_date FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.CDCNoChangeTest')";
        var createDateAfter = (System.DateTime)cmd.ExecuteScalar();
        Assert.That(createDateAfter, Is.EqualTo(createDateBefore), "Capture instance should not be recreated when no column changes");

        // Cleanup
        cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CDCNoChangeTest', @capture_instance = N'dbo_CDCNoChangeTest'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE dbo.CDCNoChangeTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
    [Test]
    public void ShouldPreserveCapturedChangeHistoryAcrossColumnAdd()
    {
        // A column change on a CDC-tracked table disables CDC before the column work and
        // re-enables it afterwards with no @capture_instance, so the capture instance --
        // and the change table holding every not-yet-consumed row -- is dropped and a fresh
        // empty one created. Asserting on captured ROWS would need the SQL Agent capture
        // job, which the test container does not run, so this asserts on the identity of
        // the change table instead: a different object_id means whatever it held is gone.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE dbo.CDCHistoryTest (Id INT IDENTITY(1,1) NOT NULL, Val NVARCHAR(100) NULL)
INSERT dbo.CDCHistoryTest (Val) VALUES ('tracked')
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCHistoryTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);

        cmd.CommandText = @"SELECT ct.object_id FROM cdc.change_tables ct
                            WHERE ct.source_object_id = OBJECT_ID('dbo.CDCHistoryTest')";
        var changeTableBefore = cmd.ExecuteScalar();
        Assert.That(changeTableBefore, Is.Not.Null, "CDC should be tracking before the deploy");

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[CDCHistoryTest]",
                "EnableCDC": true,
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false, "Identity": true},
                    {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true},
                    {"Name": "[Note]", "DataType": "NVARCHAR(50)", "Nullable": true}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        // Two instances now exist, so ask directly whether the ORIGINAL one survived rather than
        // reading "the" object_id -- an unqualified scalar read would take whichever row came back
        // first and could pass by accident.
        cmd.CommandText = @"SELECT COUNT(*) FROM cdc.change_tables ct
                            WHERE ct.source_object_id = OBJECT_ID('dbo.CDCHistoryTest')
                              AND ct.object_id = " + changeTableBefore;
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(1),
            "The original CDC capture instance was dropped, discarding every captured change a "
            + "downstream reader had not yet consumed.");

        cmd.CommandText = @"SELECT COUNT(*) FROM cdc.change_tables ct
                            WHERE ct.source_object_id = OBJECT_ID('dbo.CDCHistoryTest')";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(2),
            "Rotation should add a second capture instance covering the new column set.");

        cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CDCHistoryTest', @capture_instance = N'all'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE dbo.CDCHistoryTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
    // Turning CDC off disables each capture instance; the log names each one, and the table is recorded as dropped once.
    [Test]
    public void DisablingCdc_OnATableWithTwoCaptureInstances_NamesEach_AndRecordsTheTableOnce()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.DisableTwoInstances (Id INT NOT NULL, Val NVARCHAR(100) NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'DisableTwoInstances', @role_name = NULL, @capture_instance = N'dbo_DisableTwoInstances_a'
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'DisableTwoInstances', @role_name = NULL, @capture_instance = N'dbo_DisableTwoInstances_b'
DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        ExecuteWithDeadlockRetry(cmd);
        var messages = new System.Collections.Generic.List<string>();
        ((Microsoft.Data.SqlClient.SqlConnection)conn).InfoMessage += (_, e) =>
            messages.AddRange(System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<Microsoft.Data.SqlClient.SqlError>(e.Errors), m => m.Message));
        try
        {
            RunTableQuenchProc(cmd, """
                { "Schema": "[dbo]", "Name": "[DisableTwoInstances]", "EnableCDC": false,
                  "Columns": [ {"Name": "[Id]", "DataType": "INT", "Nullable": false}, {"Name": "[Val]", "DataType": "NVARCHAR(100)"} ] }
                """);

            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'changeDataCapture' AND ActionType = 'dropped'";
            var auditRows = (int)cmd.ExecuteScalar()!;
            Assert.Multiple(() =>
            {
                Assert.That(System.Linq.Enumerable.Where(messages, m => m.Contains("Disable CDC on")),
                    Is.EquivalentTo(new[] { "  Disable CDC on [dbo].[DisableTwoInstances] (capture instance dbo_DisableTwoInstances_a)",
                                            "  Disable CDC on [dbo].[DisableTwoInstances] (capture instance dbo_DisableTwoInstances_b)" }));
                Assert.That(auditRows, Is.EqualTo(1), "the table's capture is dropped once, whatever it held");
            });
        }
        finally
        {
            cmd.CommandText = "IF OBJECTPROPERTY(OBJECT_ID('dbo.DisableTwoInstances'), 'TableHasCDC') IS NOT NULL AND EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.DisableTwoInstances') AND is_tracked_by_cdc = 1) " +
                              "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'DisableTwoInstances', @capture_instance = N'all'; DROP TABLE IF EXISTS dbo.DisableTwoInstances";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    [Test]
    public void ShouldRefuseAColumnChangeWhenBothCaptureInstancesAreInUse()
    {
        // Rotation deliberately leaves the old instance in place -- only the operator knows when
        // downstream readers have drained it. SQL Server allows two per table, so the second
        // column change has nowhere to rotate to and must refuse up front rather than fail
        // partway through the column work or silently discard history.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE dbo.CDCCeilingTest (Id INT IDENTITY(1,1) NOT NULL, Val NVARCHAR(100) NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCCeilingTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);

        RunTableQuenchProc(cmd, CeilingPackage(withExtra: false));

        cmd.CommandText = "SELECT COUNT(*) FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.CDCCeilingTest')";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(2), "First change should have rotated to a second instance");

        var ex = Assert.Catch<System.Exception>(() => RunTableQuenchProc(cmd, CeilingPackage(withExtra: true)));
        Assert.That(ex!.Message, Does.Contain("CDC capture-instance limit reached"));
        Assert.That(ex.Message, Does.Contain("[dbo].[CDCCeilingTest]"), "the message must name the offending table");

        cmd.CommandText = "SELECT COUNT(*) FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.CDCCeilingTest')";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(2), "The refusal must not have disturbed either instance");

        cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CDCCeilingTest', @capture_instance = N'all'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE dbo.CDCCeilingTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void ARefusedDeploy_LeavesEveryOtherTablesKeysAndIndexesInPlace()
    {
        // #511: the ceiling refusal used to run after the indexes over the changing columns were dropped, so a table that
        // merely shared the deploy with a CDC table at its limit lost its primary key until a later deploy succeeded.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.CDCCeilingTest (Id INT IDENTITY(1,1) NOT NULL, Val NVARCHAR(100) NULL)
CREATE TABLE dbo.CDCCeilingPlain (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CDCCeilingPlain PRIMARY KEY, Val NVARCHAR(100) NULL)
CREATE INDEX IX_CDCCeilingPlain_Val ON dbo.CDCCeilingPlain (Val)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCCeilingTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);
        try
        {
            RunTableQuenchProc(cmd, "[" + CeilingPackage(withExtra: false) + "," + PlainPackage("INT") + "]");
            Assert.That(InstanceCount(cmd, "CDCCeilingTest"), Is.EqualTo(2), "the first change rotates to a second instance");

            // The ceiling is hit by a column CHANGE the instance cannot follow -- the identity put back, which re-creates the
            // column -- as in the report (a new column is refused earlier, before the column pass; a type change altered in
            // place no longer rotates). The first deploy's package declared Id without IDENTITY, so that swap was the first rotation.
            var ex = Assert.Catch<Exception>(() => RunTableQuenchProc(cmd, "[" + CeilingPackage(withExtra: false).Replace("\"DataType\": \"INT\", \"Nullable\": false, \"Identity\": true", "\"DataType\": \"INT IDENTITY(1, 1)\", \"Nullable\": false") + "," + PlainPackage("BIGINT") + "]"));
            Assert.That(ex!.Message, Does.Contain("CDC capture-instance limit reached"));
            var idType = GetColumnDataType(cmd, "CDCCeilingPlain", "Id");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.CDCCeilingPlain') AND [name] IN ('PK_CDCCeilingPlain', 'IX_CDCCeilingPlain_Val')";
            Assert.Multiple(() =>
            {
                Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(2), "the refusal must come before the plain table's key and index are dropped");
                Assert.That(idType, Is.EqualTo("INT IDENTITY(1, 1)"), "and before its column is altered");
            });
        }
        finally
        {
            DropCdcTable(cmd, "CDCCeilingTest");
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.CDCCeilingPlain";
            cmd.ExecuteNonQuery();
        }
    }

    private static string PlainPackage(string idType) => $$"""
          {
              "Schema": "[dbo]",
              "Name": "[CDCCeilingPlain]",
              "Columns": [
                  {"Name": "[Id]", "DataType": "{{idType}} IDENTITY(1, 1)", "Nullable": false},
                  {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true}
              ],
              "Indexes": [
                  {"Name": "[PK_CDCCeilingPlain]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]"},
                  {"Name": "[IX_CDCCeilingPlain_Val]", "IndexColumns": "[Val]"}
              ]
          }
          """;

    [Test]
    public void AColumnOptionToggle_DoesNotRotateTheCaptureInstance_AndIsNotRefusedAtTheCeiling()
    {
        // #509: NOT FOR REPLICATION, ROWGUIDCOL and a mask change nothing the capture instance sees (its columns and their
        // types), so none of them rotates it, and none is refused when both instance slots are in use.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.CDCToggleTest (Id INT IDENTITY(1,1) NOT FOR REPLICATION NOT NULL, Rg UNIQUEIDENTIFIER ROWGUIDCOL NULL, Email VARCHAR(20) MASKED WITH (FUNCTION = 'email()') NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CDCToggleTest', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);
        try
        {
            RunTableQuenchProc(cmd, TogglePackage(notForReplication: false, extraColumn: false));
            Assert.Multiple(() =>
            {
                Assert.That(InstanceCount(cmd, "CDCToggleTest"), Is.EqualTo(1), "dropping the three options must not rotate");
                Assert.That(ColumnOptions(cmd), Is.EqualTo("0,0,0"), "but must drop them (is_not_for_replication, is_rowguidcol, is_masked)");
            });

            RunTableQuenchProc(cmd, TogglePackage(notForReplication: false, extraColumn: true));
            Assert.That(InstanceCount(cmd, "CDCToggleTest"), Is.EqualTo(2), "a new column still rotates, which puts the table at the ceiling");

            RunTableQuenchProc(cmd, TogglePackage(notForReplication: true, extraColumn: true));
            Assert.Multiple(() =>
            {
                Assert.That(InstanceCount(cmd, "CDCToggleTest"), Is.EqualTo(2), "an option toggle at the ceiling is applied, not refused");
                Assert.That(ColumnOptions(cmd), Is.EqualTo("1,0,0"));
            });
        }
        finally
        {
            DropCdcTable(cmd, "CDCToggleTest");
        }
    }

    private static string ColumnOptions(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT CONVERT(VARCHAR(1), ic.is_not_for_replication) + ',' + CONVERT(VARCHAR(1), rg.is_rowguidcol) + ',' + CONVERT(VARCHAR(1), ISNULL(mc.is_masked, 0))
  FROM sys.identity_columns ic
  JOIN sys.columns rg ON rg.[object_id] = ic.[object_id] AND rg.[name] = 'Rg'
  LEFT JOIN sys.masked_columns mc ON mc.[object_id] = ic.[object_id] AND mc.[name] = 'Email'
 WHERE ic.[object_id] = OBJECT_ID('dbo.CDCToggleTest')";
        return (string)cmd.ExecuteScalar();
    }

    private static string TogglePackage(bool notForReplication, bool extraColumn)
    {
        var nfr = notForReplication ? " NOT FOR REPLICATION" : "";
        var extra = extraColumn ? ", {\"Name\": \"[Extra]\", \"DataType\": \"INT\", \"Nullable\": true}" : "";
        return $$"""
          {
              "Schema": "[dbo]",
              "Name": "[CDCToggleTest]",
              "EnableCDC": true,
              "Columns": [
                  {"Name": "[Id]", "DataType": "INT IDENTITY(1, 1){{nfr}}", "Nullable": false},
                  {"Name": "[Rg]", "DataType": "UNIQUEIDENTIFIER", "Nullable": true},
                  {"Name": "[Email]", "DataType": "VARCHAR(20)", "Nullable": true}{{extra}}
              ]
          }
          """;
    }

    private static string CeilingPackage(bool withExtra) => withExtra
        ? """
          {
              "Schema": "[dbo]",
              "Name": "[CDCCeilingTest]",
              "EnableCDC": true,
              "Columns": [
                  {"Name": "[Id]", "DataType": "INT", "Nullable": false, "Identity": true},
                  {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true},
                  {"Name": "[Note]", "DataType": "NVARCHAR(50)", "Nullable": true},
                  {"Name": "[Extra]", "DataType": "INT", "Nullable": true}
              ]
          }
          """
        : """
          {
              "Schema": "[dbo]",
              "Name": "[CDCCeilingTest]",
              "EnableCDC": true,
              "Columns": [
                  {"Name": "[Id]", "DataType": "INT", "Nullable": false, "Identity": true},
                  {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true},
                  {"Name": "[Note]", "DataType": "NVARCHAR(50)", "Nullable": true}
              ]
          }
          """;

    [Test]
    public void AColumnAlteredInPlace_DoesNotRotate_AndTheInstanceCapturesTheNewShape()
    {
        // #509: SQL Server carries an in-place ALTER COLUMN into the change table itself (probed on 2017 and 2022), so a type,
        // nullability or collation change needs no rotation -- each used to take the table's second instance slot for nothing.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.CdcInPlace (Id INT NOT NULL CONSTRAINT PK_CdcInPlace PRIMARY KEY, Val VARCHAR(10) NULL, Qty INT NULL, Note VARCHAR(20) COLLATE Latin1_General_CI_AS NULL, Code VARCHAR(50) NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcInPlace', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);
        try
        {
            cmd.CommandText = "INSERT dbo.CdcInPlace VALUES (1, 'short', 1, 'n', 'ab')";
            cmd.ExecuteNonQuery();
            WaitForCapture(cmd, "dbo_CdcInPlace_CT", "Id = 1");

            RunTableQuenchProc(cmd, """
                {
                    "Schema": "[dbo]",
                    "Name": "[CdcInPlace]",
                    "EnableCDC": true,
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "VARCHAR(50)", "Nullable": true},
                        {"Name": "[Qty]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Note]", "DataType": "VARCHAR(20)", "Nullable": true, "Collation": "Latin1_General_BIN"},
                        {"Name": "[Code]", "DataType": "VARCHAR(10)", "Nullable": true}
                    ],
                    "Indexes": [ {"Name": "[PK_CdcInPlace]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]"} ]
                }
                """);

            cmd.CommandText = "INSERT dbo.CdcInPlace VALUES (2, 'this is longer than ten chars', 2, 'N', 'cd')";
            cmd.ExecuteNonQuery();
            WaitForCapture(cmd, "dbo_CdcInPlace_CT", "Id = 2");
            cmd.CommandText = "SELECT Val + '|' + CONVERT(VARCHAR(10), Qty) + '|' + Note COLLATE DATABASE_DEFAULT + '|' + Code FROM cdc.dbo_CdcInPlace_CT WHERE Id = 2";
            var captured = (string)cmd.ExecuteScalar();
            var instances = InstanceCount(cmd, "CdcInPlace");
            var valType = GetColumnDataType(cmd, "CdcInPlace", "Val");
            Assert.Multiple(() =>
            {
                Assert.That(instances, Is.EqualTo(1), "a widening, a NOT NULL, a collation change and a narrowing the history fits must not rotate");
                Assert.That(valType, Is.EqualTo("VARCHAR(50)"), "and the change is applied");
                Assert.That(captured, Is.EqualTo("this is longer than ten chars|2|N|cd"), "and the one instance captures the widened value");
            });
        }
        finally { DropCdcTable(cmd, "CdcInPlace"); }
    }

    [Test]
    public void ATypeChangeTheChangeTableCannotTake_IsRefusedBeforeAnythingChanges()
    {
        // #518: SQL Server applies the change to every change table that captures the column, inside the capture job, and a
        // failure there stops capture for the whole database. A rotation does not avoid it (probed), so the deploy is refused.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.CdcNarrow (Id INT NOT NULL CONSTRAINT PK_CdcNarrow PRIMARY KEY, Val VARCHAR(50) NULL, Other INT NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcNarrow', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);
        try
        {
            cmd.CommandText = "INSERT dbo.CdcNarrow VALUES (1, 'a value longer than ten characters', 1)";
            cmd.ExecuteNonQuery();
            WaitForCapture(cmd, "dbo_CdcNarrow_CT", "Id = 1");
            // The source now fits the narrower type; only the change table's history does not.
            cmd.CommandText = "DELETE dbo.CdcNarrow";
            cmd.ExecuteNonQuery();
            const string package = """
                {
                    "Schema": "[dbo]",
                    "Name": "[CdcNarrow]",
                    "EnableCDC": true,
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "VARCHAR(10)", "Nullable": true},
                        {"Name": "[Other]", "DataType": "BIGINT", "Nullable": true}
                    ],
                    "Indexes": [ {"Name": "[PK_CdcNarrow]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]"} ]
                }
                """;

            var ex = Assert.Catch<Exception>(() => RunTableQuenchProc(cmd, package));
            var valType = GetColumnDataType(cmd, "CdcNarrow", "Val");
            var otherType = GetColumnDataType(cmd, "CdcNarrow", "Other");
            var instances = InstanceCount(cmd, "CdcNarrow");
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("CDC capture would stop on [dbo].[CdcNarrow]: the change to column [Val] (VARCHAR(10)"));
                Assert.That(ex.Message, Does.Contain("change table [cdc].[dbo_CdcNarrow_CT] of capture instance dbo_CdcNarrow"));
                Assert.That(ex.Message, Does.Contain("(the longest is 34 characters)").And.Not.Contain("tempdb"), "the reason is the history's, not the probe's");
                Assert.That(ex.Message, Does.Contain("@source_name = N'CdcNarrow', @capture_instance = N'dbo_CdcNarrow'"), "the message names the instance to drop");
                Assert.That(valType, Is.EqualTo("VARCHAR(50)"), "nothing on the table changed");
                Assert.That(otherType, Is.EqualTo("INT"), "not even the change that would have been fine");
                Assert.That(instances, Is.EqualTo(1));
            });

            // Doing what the message says clears the way for the same deploy.
            cmd.CommandText = "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'CdcNarrow', @capture_instance = N'dbo_CdcNarrow'";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, package);
            Assert.That(GetColumnDataType(cmd, "CdcNarrow", "Val"), Is.EqualTo("VARCHAR(10)"));
        }
        finally { DropCdcTable(cmd, "CdcNarrow"); }
    }

    [Test]
    public void ACapturedNtextColumnNarrowedPastItsHistory_IsRefusedWithTheCdcReason()
    {
        // The refusal measures the longest captured value to say how far over it is, and LEN takes no ntext; the refusal
        // must still be the one that reaches the user.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.CdcNtext (Id INT NOT NULL CONSTRAINT PK_CdcNtext PRIMARY KEY, Val NTEXT NULL)
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcNtext', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);
        try
        {
            cmd.CommandText = "INSERT dbo.CdcNtext VALUES (1, N'a value longer than ten characters')";
            cmd.ExecuteNonQuery();
            WaitForCapture(cmd, "dbo_CdcNtext_CT", "Id = 1");
            const string package = """
                {
                    "Schema": "[dbo]",
                    "Name": "[CdcNtext]",
                    "EnableCDC": true,
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "NVARCHAR(10)", "Nullable": true}
                    ],
                    "Indexes": [ {"Name": "[PK_CdcNtext]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]"} ]
                }
                """;

            var ex = Assert.Catch<Exception>(() => RunTableQuenchProc(cmd, package));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("CDC capture would stop on [dbo].[CdcNtext]: the change to column [Val] (NVARCHAR(10)"));
                Assert.That(ex.Message, Does.Not.Contain("is invalid for argument 1 of len function"));
                Assert.That(GetColumnDataType(cmd, "CdcNtext", "Val"), Is.EqualTo("NTEXT"), "nothing on the table changed");
            });
        }
        finally { DropCdcTable(cmd, "CdcNtext"); }
    }

    [Test]
    public void ChangingAComputedColumnsExpression_DoesNotRotate()
    {
        // A captured computed column is always NULL (SQL Server documents it, and the probe agreed), so re-creating it with
        // another expression changes nothing the instance records.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.CdcComputedExpr (Id INT NOT NULL CONSTRAINT PK_CdcComputedExpr PRIMARY KEY, A INT NULL, Twice AS ([A] * 2))
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcComputedExpr', @role_name = NULL";
        ExecuteWithDeadlockRetry(cmd);
        try
        {
            RunTableQuenchProc(cmd, """
                {
                    "Schema": "[dbo]",
                    "Name": "[CdcComputedExpr]",
                    "EnableCDC": true,
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[A]", "DataType": "INT", "Nullable": true},
                        {"Name": "[Twice]", "DataType": "INT", "Nullable": true, "ComputedExpression": "[A] * 3"}
                    ],
                    "Indexes": [ {"Name": "[PK_CdcComputedExpr]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]"} ]
                }
                """);
            cmd.CommandText = "SELECT [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('dbo.CdcComputedExpr') AND [name] = 'Twice'";
            var definition = (string)cmd.ExecuteScalar();
            var instances = InstanceCount(cmd, "CdcComputedExpr");
            Assert.Multiple(() =>
            {
                Assert.That(definition, Is.EqualTo("([A]*(3))"), "the expression is changed");
                Assert.That(instances, Is.EqualTo(1), "without a rotation");
                Assert.That(NewestInstanceColumns(cmd, "CdcComputedExpr"), Is.EqualTo(new[] { "A", "Id", "Twice" }));
            });
        }
        finally { DropCdcTable(cmd, "CdcComputedExpr"); }
    }

    // A captured row exists once the capture job has scanned the log. The Agent runs that job where there is one (the demo
    // server) and nowhere in CI; sys.sp_cdc_scan runs the same scan on demand in both cases (probed), so this never waits
    // on the job's schedule, and tolerates the call failing while the job holds the scan.
    private static void WaitForCapture(IDbCommand cmd, string changeTable, string predicate)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cmd.CommandText = "BEGIN TRY EXEC sys.sp_cdc_scan @maxtrans = 500, @maxscans = 10, @continuous = 0 END TRY BEGIN CATCH END CATCH";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"SELECT COUNT(*) FROM cdc.{changeTable} WITH (NOLOCK) WHERE {predicate}";
            if ((int)cmd.ExecuteScalar() > 0) return;
            Thread.Sleep(500);
        }
        Assert.Fail($"cdc.{changeTable} never received a row where {predicate}");
    }

    // ---------------------------------------------------------------------------------------------------
    // CdcFilegroup (#417). Change tables go where the package says; a table whose capture instance sits
    // somewhere else is ROTATED onto the declared filegroup, never moved -- the old instance keeps its
    // history. Unset means unmanaged, at both tiers.
    // ---------------------------------------------------------------------------------------------------

    private const string CdcFilegroup = "SchemaSmithCdcFg";

    [OneTimeSetUp]
    public void CreateCdcFilegroup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [name] = '{CdcFilegroup}')
BEGIN
    DECLARE @db SYSNAME = DB_NAME()
    DECLARE @path NVARCHAR(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS NVARCHAR(4000)) + @db + '_{CdcFilegroup}.ndf'
    DECLARE @sql NVARCHAR(MAX) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILEGROUP [{CdcFilegroup}]'
    EXEC(@sql)
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILE (NAME = N''' + @db + N'_{CdcFilegroup}'', FILENAME = N''' + @path + N''', SIZE = 8MB) TO FILEGROUP [{CdcFilegroup}]'
    EXEC(@sql)
END";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Disable every capture instance this fixture could have left behind, whichever test failed.
    /// <para>A CDC-enabled table keeps SQL Server's capture agent (<c>sp_cdc_scan</c>) alive in the
    /// database, polling on a WAITFOR. Only 3 of this fixture's 16 tests wrapped their cleanup in a
    /// finally, so any failure part-way left the agent running — and a database with a live capture agent
    /// resists teardown, so the test database leaks and whatever waits on it stalls. That is not
    /// hypothetical: a surviving agent held a full four-engine gate for 72 minutes at ~3% CPU before it
    /// was killed by hand, and the databases it stranded had to be dropped manually.</para>
    /// <para>Fixture-level rather than per-test on purpose: 16 finally blocks is 16 chances to forget one,
    /// and a test added later would not know to write it. This runs regardless of how a test exited.</para>
    /// </summary>
    [OneTimeTearDown]
    public void DisableAnyCaptureInstancesLeftRunning()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = @"
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N''
    SELECT @sql = @sql + N'EXEC sys.sp_cdc_disable_table @source_schema = N''''' + s.[name]
                       + N''''', @source_name = N''''' + o.[name] + N''''', @capture_instance = N''''all'''';' + CHAR(10)
      FROM cdc.change_tables ct
      JOIN sys.objects o ON o.[object_id] = ct.source_object_id
      JOIN sys.schemas s ON s.[schema_id] = o.[schema_id]
     GROUP BY s.[name], o.[name]
    IF @sql <> N'' EXEC sp_executesql @sql
END";
            cmd.ExecuteNonQuery();
        }
        catch (DbException)
        {
            // Teardown must never mask a test failure. If the agent cannot be stopped here, FixtureSetup's
            // database-level drop still runs; this is the cheap first attempt, not the only safeguard.
        }
    }

    private void RunCdcQuench(IDbCommand cmd, string json, string templateCdcFilegroup = null)
    {
        cmd.CommandTimeout = 300;
        var fg = templateCdcFilegroup == null ? "NULL" : $"N'{templateCdcFilegroup}'";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0, @CdcFilegroup = {fg}";
        ExecuteWithDeadlockRetry(cmd);
    }

    private static string CdcTableJson(string table, string cdcFilegroup = null, bool extraColumn = false)
    {
        var fg = cdcFilegroup == null ? "" : $"\"CdcFilegroup\": \"{cdcFilegroup}\",";
        var extra = extraColumn ? ", {\"Name\": \"[Extra]\", \"DataType\": \"INT\", \"Nullable\": true}" : "";
        return "{\"Schema\": \"[dbo]\", \"Name\": \"[" + table + "]\", \"EnableCDC\": true, " + fg
             + "\"Columns\": [{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false}, {\"Name\": \"[Val]\", \"DataType\": \"NVARCHAR(100)\", \"Nullable\": true}" + extra + "]}";
    }

    // The filegroup the NEWEST capture instance's change table lives on, resolving the catalog's NULL
    // ("the database default") to the default's actual name so a comparison can never pass on NULL.
    private static string NewestInstanceFilegroup(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT TOP 1 ISNULL(ct.filegroup_name, (SELECT [name] FROM sys.filegroups WHERE is_default = 1))
                               FROM cdc.change_tables ct
                              WHERE ct.source_object_id = OBJECT_ID('dbo.{table}')
                              ORDER BY ct.create_date DESC, ct.object_id DESC";
        return cmd.ExecuteScalar() as string;
    }

    private static int InstanceCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.{table}')";
        return (int)cmd.ExecuteScalar();
    }

    private static string DefaultFilegroup(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT [name] FROM sys.filegroups WHERE is_default = 1";
        return (string)cmd.ExecuteScalar();
    }

    private static void DropCdcTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.{table}'))
        EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'{table}', @capture_instance = N'all'
    DROP TABLE dbo.{table}
END";
        cmd.ExecuteNonQuery();
    }

    private void CreateTable(IDbCommand cmd, string table, string enableOnFilegroup = null, bool enable = false)
    {
        DropCdcTable(cmd, table);
        var enableSql = !enable && enableOnFilegroup == null
            ? ""
            : $"\nEXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'{table}', @role_name = NULL"
              + (enableOnFilegroup == null ? "" : $", @filegroup_name = N'{enableOnFilegroup}'");
        cmd.CommandText = $"CREATE TABLE dbo.{table} (Id INT NOT NULL, Val NVARCHAR(100) NULL){enableSql}";
        ExecuteWithDeadlockRetry(cmd);
    }

    private void WithMainDb(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try { body(cmd); }
        finally { DropCdcTable(cmd, table); }
    }

    [Test]
    public void CdcFilegroup_OnTheTable_PlacesTheChangeTableThere() =>
        WithMainDb("CdcFgTableLevel", cmd =>
        {
            CreateTable(cmd, "CdcFgTableLevel");

            RunCdcQuench(cmd, CdcTableJson("CdcFgTableLevel", CdcFilegroup));

            Assert.That(NewestInstanceFilegroup(cmd, "CdcFgTableLevel"), Is.EqualTo(CdcFilegroup));
        });

    [Test]
    public void CdcFilegroup_FromTheTemplate_AppliesToATableThatDeclaresNone() =>
        WithMainDb("CdcFgTemplateDefault", cmd =>
        {
            CreateTable(cmd, "CdcFgTemplateDefault");

            RunCdcQuench(cmd, CdcTableJson("CdcFgTemplateDefault"), templateCdcFilegroup: CdcFilegroup);

            Assert.That(NewestInstanceFilegroup(cmd, "CdcFgTemplateDefault"), Is.EqualTo(CdcFilegroup));
        });

    [Test]
    public void CdcFilegroup_OnTheTable_OverridesTheTemplateDefault() =>
        WithMainDb("CdcFgTableOverrides", cmd =>
        {
            CreateTable(cmd, "CdcFgTableOverrides");
            var primary = DefaultFilegroup(cmd);

            RunCdcQuench(cmd, CdcTableJson("CdcFgTableOverrides", primary), templateCdcFilegroup: CdcFilegroup);

            Assert.That(NewestInstanceFilegroup(cmd, "CdcFgTableOverrides"), Is.EqualTo(primary));
        });

    [Test]
    public void CdcFilegroup_Changed_RotatesOntoIt_KeepsTheOldInstance_AndThenConverges() =>
        WithMainDb("CdcFgRotate", cmd =>
        {
            CreateTable(cmd, "CdcFgRotate", enable: true);
            cmd.CommandText = "SELECT object_id FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.CdcFgRotate')";
            var originalChangeTable = cmd.ExecuteScalar();

            RunCdcQuench(cmd, CdcTableJson("CdcFgRotate", CdcFilegroup));

            Assert.That(NewestInstanceFilegroup(cmd, "CdcFgRotate"), Is.EqualTo(CdcFilegroup), "the new instance lands on the declared filegroup");
            Assert.That(InstanceCount(cmd, "CdcFgRotate"), Is.EqualTo(2), "rotation adds an instance rather than replacing one");
            cmd.CommandText = $"SELECT COUNT(*) FROM cdc.change_tables WHERE object_id = {originalChangeTable}";
            Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(1), "the original instance and its history must survive");

            RunCdcQuench(cmd, CdcTableJson("CdcFgRotate", CdcFilegroup));

            Assert.That(InstanceCount(cmd, "CdcFgRotate"), Is.EqualTo(2), "a second deploy of the same declaration must change nothing");
        });

    [Test]
    public void CdcFilegroup_ChangedWhileBothInstancesAreInUse_IsRefused() =>
        WithMainDb("CdcFgCeiling", cmd =>
        {
            CreateTable(cmd, "CdcFgCeiling", enable: true);
            cmd.CommandText = "EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcFgCeiling', @capture_instance = N'dbo_CdcFgCeiling_2', @role_name = NULL";
            ExecuteWithDeadlockRetry(cmd);

            var ex = Assert.Catch<Exception>(() => RunCdcQuench(cmd, CdcTableJson("CdcFgCeiling", CdcFilegroup)));

            Assert.That(ex!.Message, Does.Contain("CDC capture-instance limit reached").And.Contain("CdcFgCeiling"));
            Assert.That(InstanceCount(cmd, "CdcFgCeiling"), Is.EqualTo(2), "the refusal must not disturb either instance");
        });

    // Unset means unmanaged: a DBA's placement survives a package that never mentions CdcFilegroup.
    [Test]
    public void CdcFilegroup_Unset_LeavesAnExistingNonDefaultPlacementAlone() =>
        WithMainDb("CdcFgUnmanaged", cmd =>
        {
            CreateTable(cmd, "CdcFgUnmanaged", enableOnFilegroup: CdcFilegroup);

            RunCdcQuench(cmd, CdcTableJson("CdcFgUnmanaged"));

            Assert.That(InstanceCount(cmd, "CdcFgUnmanaged"), Is.EqualTo(1), "nothing declared, nothing to rotate");
            Assert.That(NewestInstanceFilegroup(cmd, "CdcFgUnmanaged"), Is.EqualTo(CdcFilegroup));
        });

    // The latent defect #417 exposed: a column-change rotation omitted @filegroup_name, so a table whose
    // change capture a DBA routed to a dedicated filegroup had it silently moved to the default.
    [Test]
    public void ColumnChangeRotation_WithNothingDeclared_StaysOnTheExistingFilegroup() =>
        WithMainDb("CdcFgColumnRotation", cmd =>
        {
            CreateTable(cmd, "CdcFgColumnRotation", enableOnFilegroup: CdcFilegroup);

            RunCdcQuench(cmd, CdcTableJson("CdcFgColumnRotation", extraColumn: true));

            Assert.That(InstanceCount(cmd, "CdcFgColumnRotation"), Is.EqualTo(2), "precondition: the column change rotated");
            Assert.That(NewestInstanceFilegroup(cmd, "CdcFgColumnRotation"), Is.EqualTo(CdcFilegroup),
                "the rotated instance must stay on the filegroup the original was on");
        });

    [Test]
    public void CdcFilegroup_ThatDoesNotExist_FailsByName_BeforeEnabling() =>
        WithMainDb("CdcFgMissing", cmd =>
        {
            CreateTable(cmd, "CdcFgMissing");

            var ex = Assert.Catch<Exception>(() => RunCdcQuench(cmd, CdcTableJson("CdcFgMissing", "NoSuchCdcFg")));

            Assert.That(ex!.Message, Does.Contain("NoSuchCdcFg").And.Contain("CdcFgMissing").And.Contain("CdcFilegroup"));
            cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.CdcFgMissing')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(false));
        });
}
