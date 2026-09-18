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
    public void ShouldRefreshCDCAfterDropColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        try
        {
            // IDEMPOTENT SETUP, and it is load-bearing: this batch runs under ExecuteWithDeadlockRetry, and
            // sp_cdc_enable_table is deadlock-prone against the CDC scan job. A retry after the CREATE has
            // already succeeded re-runs the whole batch and fails with "There is already an object named
            // 'CDCDropColTest'" -- which is what this test did in CI, intermittently and for years, and it
            // reads like a product failure rather than a retried batch.
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

            // Assert CDC is still enabled
            cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE object_id = OBJECT_ID('dbo.CDCDropColTest')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "CDC should remain enabled");

            // Assert the capture instance now tracks 2 columns (Extra is gone)
            // Rotation leaves BOTH instances in place, so scope this to the one this deploy created --
            // an unscoped count sums the old and new column sets and means nothing.
            cmd.CommandText = @"SELECT COUNT(*) FROM cdc.captured_columns cc
                                WHERE cc.object_id = (SELECT TOP 1 ct.object_id FROM cdc.change_tables ct
                                                       WHERE ct.source_object_id = OBJECT_ID('dbo.CDCDropColTest')
                                                       ORDER BY ct.create_date DESC, ct.object_id DESC)";
            Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(2), "Refreshed capture instance should track only 2 columns");
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
    /// <para>Every instance, not just <c>dbo_CDCDropColTest</c>: the rotation this test exercises
    /// deliberately leaves the original instance in place alongside the new one, so naming a single
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
