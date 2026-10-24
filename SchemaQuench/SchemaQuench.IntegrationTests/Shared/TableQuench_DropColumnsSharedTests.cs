// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for dropping columns during table quench.
/// Tests that columns with dependencies (indexes, FKs, check constraints) are properly handled.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_DropColumnsSharedTests : BaseTableQuenchTests
{
    private const string TestSchema = "DropColumnsTests";

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnUsedInIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Column2 should be dropped
        Assert.That(ColumnExists(cmd, "DropColumnInIndex", "Column2"), Is.False);

        // Column1 should still exist
        Assert.That(ColumnExists(cmd, "DropColumnInIndex", "Column1"), Is.True);

        // IDX_Dependency should be dropped (was on Column2)
        Assert.That(IndexExists(cmd, "DropColumnInIndex", "IDX_Dependency"), Is.False);

        // IDX_NoDependency should still exist (on Column1)
        Assert.That(IndexExists(cmd, "DropColumnInIndex", "IDX_NoDependency"), Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnUsedInUniqueIndexReferencedByForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Column2 should be dropped
        Assert.That(ColumnExists(cmd, "DropColumnInUniqueIndexInFK", "Column2"), Is.False);

        // Column1 should still exist
        Assert.That(ColumnExists(cmd, "DropColumnInUniqueIndexInFK", "Column1"), Is.True);

        // Unique index should be dropped
        Assert.That(IndexExists(cmd, "DropColumnInUniqueIndexInFK", "IDX_Dependency2"), Is.False);

        // FK should be dropped
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'DropColumnInUniqueIndexInFK'
              AND CONSTRAINT_NAME = 'FK_DropColumnInUniqueIndexInFK_SelfRef'
              AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.That(ColumnExists(cmd, "DropColumnWithDefault", "Column2"), Is.False);
        Assert.That(ColumnExists(cmd, "DropColumnWithDefault", "Column1"), Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithTableLevelCheckConstraint()
    {
        // Below MariaDB 10.2.22 a CHECK cannot be read, so it is not dropped ahead of a column it reads, and the engine refuses
        // the column's drop. Documented in the SchemaQuench engine-compatibility notes.
        if (Platform == Platform.MariaDb && !TestVersionGates.SupportsCheckConstraints(Platform, _connectionString))
            Assert.Ignore("A CHECK cannot be read below MariaDB 10.2.22.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.That(ColumnExists(cmd, "DropColumnWithTableCheckConstraint", "Column2"), Is.False);
        Assert.That(ColumnExists(cmd, "DropColumnWithTableCheckConstraint", "Column1"), Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.That(ColumnExists(cmd, "DropColumnWithFK", "Column2"), Is.False);
        Assert.That(ColumnExists(cmd, "DropColumnWithFK", "Column1"), Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithComputedExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Column2 is referenced by Column3's generated expression, so both should handle correctly
        Assert.That(ColumnExists(cmd, "DropColumnWithComputed", "Column2"), Is.False);
        Assert.That(ColumnExists(cmd, "DropColumnWithComputed", "Column1"), Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldSuppressDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // The suppressed table: OrphanedColumn was removed from JSON + flag = false → column survives
        Assert.That(ColumnExists(cmd, "DropColumnSuppressed", "OrphanedColumn"), Is.True,
            "OrphanedColumn should still exist (suppressed by table flag)");

        // The control table: flag absent (inherits default = true) → column was dropped
        Assert.That(ColumnExists(cmd, "DropColumnControl", "OrphanedColumn"), Is.False,
            "OrphanedColumn should be gone (no suppression flag)");

        conn.Close();
    }

    private new bool ColumnExists(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = '{table}'
              AND COLUMN_NAME = '{column}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private new bool IndexExists(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = '{table}'
              AND INDEX_NAME = '{index}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Drop and recreate test schema to ensure clean state (prior interrupted runs may leave residual objects)
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE DATABASE `{TestSchema}`";
        cmd.ExecuteNonQuery();

        cmd.CommandTimeout = 300;
        // MySQL before 8.0.16 parses a CHECK and ignores it, so the table is created with it everywhere but early MariaDB.
        var check = Platform != Platform.MariaDb || TestVersionGates.SupportsCheckConstraints(Platform, _connectionString)
            ? ", CONSTRAINT `CK_DropColumnWithTableCheckConstraint_Dependency` CHECK (`Column2` < `Column1`)"
            : "";
        cmd.CommandText = $@"
-- TableQuench_ShouldHandleRemovingColumnUsedInIndex
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnInIndex` (`Column1` INT NOT NULL, `Column2` INT);
CREATE INDEX `IDX_NoDependency` ON `{TestSchema}`.`DropColumnInIndex` (`Column1`);
CREATE INDEX `IDX_Dependency` ON `{TestSchema}`.`DropColumnInIndex` (`Column2`);
-- TableQuench_ShouldHandleRemovingColumnUsedInUniqueIndexReferencedByForeignKey
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnInUniqueIndexInFK` (`Column1` INT NOT NULL, `Column2` INT NOT NULL);
CREATE UNIQUE INDEX `IDX_Dependency2` ON `{TestSchema}`.`DropColumnInUniqueIndexInFK` (`Column2`);
ALTER TABLE `{TestSchema}`.`DropColumnInUniqueIndexInFK` ADD CONSTRAINT `FK_DropColumnInUniqueIndexInFK_SelfRef` FOREIGN KEY (`Column1`) REFERENCES `{TestSchema}`.`DropColumnInUniqueIndexInFK` (`Column2`);
-- TableQuench_ShouldHandleRemovingColumnWithDefault
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnWithDefault` (`Column1` INT NOT NULL, `Column2` INT DEFAULT 0);
-- TableQuench_ShouldHandleRemovingColumnWithTableLevelCheckConstraint
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnWithTableCheckConstraint` (`Column1` INT NOT NULL, `Column2` INT{check});
-- TableQuench_ShouldHandleRemovingColumnWithForeignKey
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnWithFK` (`Column1` INT NOT NULL, `Column2` INT, PRIMARY KEY (`Column1`));
CREATE UNIQUE INDEX `UQ_Column2` ON `{TestSchema}`.`DropColumnWithFK` (`Column2`);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnWithFKRef` (`Column1` INT NOT NULL, `Column2` INT, PRIMARY KEY (`Column1`));
-- TableQuench_ShouldHandleRemovingColumnWithComputedExpression
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnWithComputed` (`Column1` INT NOT NULL, `Column2` INT, `Column3` INT AS (`Column2` * 3) VIRTUAL);
-- TableQuench_ShouldSuppressDropWhenTableFlagIsFalse
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnSuppressed` (`Column1` INT NOT NULL, `OrphanedColumn` INT);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`DropColumnControl` (`Column1` INT NOT NULL, `OrphanedColumn` INT);
";
        cmd.ExecuteNonQuery();

        // JSON with columns removed
        var json = """
            [
            {
                "Name": "DropColumnInIndex",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            },
            {
                "Name": "DropColumnInUniqueIndexInFK",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            },
            {
                "Name": "DropColumnWithDefault",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            },
            {
                "Name": "DropColumnWithTableCheckConstraint",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            },
            {
                "Name": "DropColumnWithFK",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            },
            {
                "Name": "DropColumnWithComputed",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false },
                    { "Name": "Column3", "DataType": "INT", "Nullable": true, "GenerationExpression": "`Column1` * 3", "Generated": "VIRTUAL" }
                ]
            }
            ]
            """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Trailing 0, 0 are DropUnknownIndexes and DropIndexesRemovedFromProduct. Index removal lives
        // in this procedure now, but this fixture is about COLUMN drops: the definitions above declare
        // no indexes, the catalog indexes created in the DDL block are unowned, and nothing here
        // recreates an index — so both flags stay off and no index is touched.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Second quench: exercises table-level DropColumnsRemovedFromProduct flag.
        // DropColumnSuppressed carries "DropColumnsRemovedFromProduct": false → OrphanedColumn survives.
        // DropColumnControl has no flag (null → inherits cascade default=true) → OrphanedColumn drops.
        var flagJson = """
            [
            {
                "Name": "DropColumnSuppressed",
                "DropColumnsRemovedFromProduct": false,
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            },
            {
                "Name": "DropColumnControl",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ]
            }
            ]
            """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{flagJson.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Index-drop flags off, as above — this quench only exercises column removal.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        conn.Close();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch { /* Ignore cleanup errors */ }
    }
}
