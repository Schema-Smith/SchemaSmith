// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for adding missing items during table quench.
/// Tests adding indexes, columns, defaults, check constraints, and foreign keys.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_AddMissingItemsSharedTests : BaseTableQuenchTests
{
    private const string TestSchema = "AddMissingItemsTests";

    [Test]
    public void TableQuench_ShouldAddMissingIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Index should exist
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyIndex'
              AND INDEX_NAME = 'IDX_NewIndex'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        // Make sure the index ownership is recorded
        cmd.CommandText = $@"
            SELECT ProductName FROM `{_mainDb}`.SchemaSmith_ProductOwnership
            WHERE ObjectSchema = '{TestSchema}'
              AND ObjectName = 'AddMyIndex.IDX_NewIndex'
              AND ObjectType = 'INDEX'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(_productName));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingIndexForIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyIndexIO'
              AND INDEX_NAME = 'IDX_NewIndexIO'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        // Make sure the index ownership is recorded
        cmd.CommandText = $@"
            SELECT ProductName FROM `{_mainDb}`.SchemaSmith_ProductOwnership
            WHERE ObjectSchema = '{TestSchema}'
              AND ObjectName = 'AddMyIndexIO.IDX_NewIndexIO'
              AND ObjectType = 'INDEX'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(_productName));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // NewColumn should exist
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyColumn'
              AND COLUMN_NAME = 'NewColumn'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        // CollatedColumn should exist
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyColumn'
              AND COLUMN_NAME = 'CollatedColumn'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        // DontApply should NOT exist (ShouldApplyExpression = false)
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyColumn'
              AND COLUMN_NAME = 'DontApply'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyDefault'
              AND COLUMN_NAME = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingTableWithEnumDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // ENUM column should have correct default (UPPER due to ParseTableJson UPPER(DataType))
        cmd.CommandText = $@"
            SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyEnumDefault'
              AND COLUMN_NAME = 'Status'";
        // MariaDB reports string/ENUM COLUMN_DEFAULT quoted ('Active'); MySQL bare (Active). Strip to compare intent.
        Assert.That(cmd.ExecuteScalar()?.ToString()?.Trim('\''), Is.EqualTo("Active").IgnoreCase);

        // VARCHAR column should have correct default
        cmd.CommandText = $@"
            SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyEnumDefault'
              AND COLUMN_NAME = 'Label'";
        Assert.That(cmd.ExecuteScalar()?.ToString()?.Trim('\''), Is.EqualTo("unknown").IgnoreCase);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingColumnWithStringDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Column should exist
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyStringDefault'
              AND COLUMN_NAME = 'NewStatus'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        // ENUM column should have correct default
        cmd.CommandText = $@"
            SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyStringDefault'
              AND COLUMN_NAME = 'NewStatus'";
        Assert.That(cmd.ExecuteScalar()?.ToString()?.Trim('\''), Is.EqualTo("Pending").IgnoreCase);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingTableLevelCheckConstraint()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyTableCheck'
              AND CONSTRAINT_NAME = 'CHK_AddMyTableCheck_MyCheck'
              AND CONSTRAINT_TYPE = 'CHECK'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyFK'
              AND CONSTRAINT_NAME = 'FK_AddMyFK_SelfRef'
              AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingUniqueIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyUniqueIndex'
              AND INDEX_NAME = 'IDX_UniqueColumn'
              AND SEQ_IN_INDEX = 1";
        var nonUnique = cmd.ExecuteScalar();
        Assert.That(nonUnique, Is.Not.Null);
        Assert.That(Convert.ToInt32(nonUnique), Is.EqualTo(0), "Index should be unique");

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameColumnsHaveMutuallyExclusiveShouldApply()
    {
        // Regression test for the silent-divergence bug surfaced 2026-06-01: when a table JSON
        // contained two same-named column entries with mutually exclusive ShouldApplyExpression,
        // both rows were silently dropped during JSON parsing and the column never landed. The
        // generated per-row UPDATE-then-DELETE statements matched on column name only, so any
        // one row whose expression evaluated false would zero the ShouldApply flag on every row
        // sharing the name -- including the sibling that was supposed to survive.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // The variant whose ShouldApplyExpression evaluates true ("true") should survive
        // with its declared type (INT). The other variant (VARCHAR(50), gated on "false") is skipped.
        cmd.CommandText = $@"
            SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyVariantColumn'
              AND COLUMN_NAME = 'payload'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("int"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameIndexesHaveMutuallyExclusiveShouldApply()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // The surviving variant indexes col1; the skipped variant targeted col2.
        cmd.CommandText = $@"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyVariantIndex'
              AND INDEX_NAME = 'IDX_Variant'
            ORDER BY SEQ_IN_INDEX";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("col1"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameFKsHaveMutuallyExclusiveShouldApply()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE CONSTRAINT_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyVariantFK'
              AND CONSTRAINT_NAME = 'FK_AddMyVariantFK_Variant'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("col1"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameCheckConstraintsHaveMutuallyExclusiveShouldApply()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = '{TestSchema}'
              AND CONSTRAINT_NAME = 'CHK_AddMyVariantCheck_Variant'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("`col1` > 0"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldEchoVariantNameInOperationMessages()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS `{TestSchema}`.`VariantLogTest` (`Id` INT NOT NULL, `col1` INT NOT NULL, `col2` INT NOT NULL)";
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Name": "VariantLogTest",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": false },
                    { "Name": "col2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_VariantLog", "IndexColumns": "col1", "ShouldApplyExpression": "1=1", "VariantName": "Modern engines" },
                    { "Name": "IDX_VariantLog", "IndexColumns": "col2", "ShouldApplyExpression": "1=0", "VariantName": "Legacy engines" }
                ]
            }
            ]
            """;

        try
        {
            cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
            cmd.ExecuteNonQuery();
            // Index rename/modify/removal live in ModifiedTableQuench; the create half stays below.
            // Every non-index drop flag is 0 so this call cannot drop the table or any column the
            // test did not previously drop, and DropIndexesRemovedFromProduct carries over the 1 the
            // MissingIndexesAndConstraintsQuench call used to pass.
            cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 0, 1, 1, 1, 0, 0, 1)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
            cmd.ExecuteNonQuery();

            // SchemaSmith_StatusMessages is keyed by CONNECTION_ID(); query on the SAME connection.
            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%(variant: Modern engines)%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThanOrEqualTo(1));

            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%Legacy engines%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{TestSchema}`.`VariantLogTest`";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMultipleColumnsAndIndexesToExistingTableInOneQuench()
    {
        // A large convergence refactor folds multiple per-table operations into one statement
        // (MySQL: GROUP_CONCAT multi-clause ALTER TABLE ... ADD COLUMN a, ADD COLUMN b, ADD INDEX x, ...).
        // This exercises that fold path directly: 2+ new columns AND 2+ new non-PK indexes land on
        // the SAME table in a SINGLE quench run.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItems'
              AND COLUMN_NAME = 'Column1'
              AND DATA_TYPE = 'varchar'
              AND IS_NULLABLE = 'YES'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItems'
              AND COLUMN_NAME = 'Column2'
              AND DATA_TYPE = 'int'
              AND IS_NULLABLE = 'YES'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItems'
              AND INDEX_NAME = 'IDX_MultiColumn1'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItems'
              AND INDEX_NAME = 'IDX_MultiColumn2'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldBootstrapTableWithMultipleColumnsAndIndexesInOneQuench()
    {
        // Same fold path, but for a table that does not pre-exist: the table itself, its non-key
        // columns, and its indexes are all created together from a single JSON table definition.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItemsBootstrap'
              AND COLUMN_NAME = 'Column1'
              AND DATA_TYPE = 'varchar'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItemsBootstrap'
              AND COLUMN_NAME = 'Column2'
              AND DATA_TYPE = 'int'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItemsBootstrap'
              AND INDEX_NAME = 'IDX_BootstrapColumn1'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'AddMyMultipleItemsBootstrap'
              AND INDEX_NAME = 'IDX_BootstrapColumn2'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0));

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Drop and recreate to ensure clean state
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CREATE DATABASE `{TestSchema}`";
        cmd.ExecuteNonQuery();

        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"
-- TableQuench_ShouldAddMissingIndex
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyIndex` (`Id` INT NOT NULL);
-- TableQuench_ShouldAddMissingColumn
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyColumn` (`Id` INT NOT NULL);
-- TableQuench_ShouldAddMissingDefault
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyDefault` (`Id` INT NOT NULL);
-- TableQuench_ShouldAddMissingTableLevelCheckConstraint
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyTableCheck` (`Id` INT NOT NULL, `Col2` INT);
-- TableQuench_ShouldAddMissingForeignKey
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyFK` (`Id` INT NOT NULL PRIMARY KEY, `Col2` INT);
-- TableQuench_ShouldAddMissingUniqueIndex
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyUniqueIndex` (`Id` INT NOT NULL, `UniqueColumn` VARCHAR(50) NOT NULL);

-- TableQuench_ShouldAddMissingColumnWithStringDefault
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyStringDefault` (`Id` INT NOT NULL);
-- TableQuench_ShouldKeepOneVariantWhenTwoSameNameColumnsHaveMutuallyExclusiveShouldApply
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyVariantColumn` (`Id` INT NOT NULL);
-- TableQuench_ShouldKeepOneVariantWhenTwoSameNameIndexesHaveMutuallyExclusiveShouldApply
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyVariantIndex` (`Id` INT NOT NULL, `col1` INT NOT NULL, `col2` INT NOT NULL);
-- TableQuench_ShouldKeepOneVariantWhenTwoSameNameFKsHaveMutuallyExclusiveShouldApply
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyVariantFK` (`Id` INT NOT NULL PRIMARY KEY, `col1` INT NULL, `col2` INT NULL, KEY ix_variant_col1 (`col1`), KEY ix_variant_col2 (`col2`));
-- TableQuench_ShouldKeepOneVariantWhenTwoSameNameCheckConstraintsHaveMutuallyExclusiveShouldApply
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyVariantCheck` (`Id` INT NOT NULL, `col1` INT NULL);
-- TableQuench_ShouldAddMultipleColumnsAndIndexesToExistingTableInOneQuench
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyMultipleItems` (`Id` INT NOT NULL);
-- TableQuench_ShouldBootstrapTableWithMultipleColumnsAndIndexesInOneQuench (table created entirely by the quench)

-- Index Only
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`AddMyIndexIO` (`Id` INT NOT NULL);
";
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Name": "AddMyIndex",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_NewIndex", "IndexColumns": "Id" }
                ]
            },
            {
                "Name": "AddMyColumn",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "NewColumn", "DataType": "VARCHAR(10)", "Nullable": true },
                    { "Name": "CollatedColumn", "DataType": "VARCHAR(10)", "Nullable": true, "Collation": "utf8mb4_unicode_ci" },
                    { "Name": "DontApply", "DataType": "INT", "Nullable": true, "ShouldApplyExpression": "false" }
                ]
            },
            {
                "Name": "AddMyDefault",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false, "Default": "0" }
                ]
            },
            {
                "Name": "AddMyTableCheck",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true }
                ],
                "CheckConstraints": [
                    { "Name": "CHK_AddMyTableCheck_MyCheck", "Expression": "`Id`<`Col2`" }
                ]
            },
            {
                "Name": "AddMyFK",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" }
                ],
                "ForeignKeys": [
                    {
                        "Name": "FK_AddMyFK_SelfRef",
                        "Columns": "Col2",
                        "RelatedTable": "AddMyFK",
                        "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Name": "AddMyEnumDefault",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Status", "DataType": "ENUM('Active','Inactive','Pending')", "Nullable": false, "Default": "'Active'" },
                    { "Name": "Label", "DataType": "VARCHAR(50)", "Nullable": true, "Default": "'unknown'" }
                ]
            },
            {
                "Name": "AddMyStringDefault",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "NewStatus", "DataType": "ENUM('Active','Inactive','Pending')", "Nullable": true, "Default": "'Pending'" }
                ]
            },
            {
                "Name": "AddMyUniqueIndex",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "UniqueColumn", "DataType": "VARCHAR(50)", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_UniqueColumn", "IndexColumns": "UniqueColumn", "Unique": true }
                ]
            },
            {
                "Name": "AddMyVariantColumn",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "payload", "DataType": "INT", "Nullable": true, "ShouldApplyExpression": "true" },
                    { "Name": "payload", "DataType": "VARCHAR(50)", "Nullable": true, "ShouldApplyExpression": "false" }
                ]
            },
            {
                "Name": "AddMyVariantIndex",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": false },
                    { "Name": "col2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_Variant", "IndexColumns": "col1", "ShouldApplyExpression": "true" },
                    { "Name": "IDX_Variant", "IndexColumns": "col2", "ShouldApplyExpression": "false" }
                ]
            },
            {
                "Name": "AddMyVariantFK",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": true },
                    { "Name": "col2", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" },
                    { "Name": "ix_variant_col1", "IndexColumns": "col1" },
                    { "Name": "ix_variant_col2", "IndexColumns": "col2" }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_AddMyVariantFK_Variant",
                      "Columns": "col1",
                      "RelatedTable": "AddMyVariantFK",
                      "RelatedColumns": "Id",
                      "ShouldApplyExpression": "true"
                    },
                    {
                      "Name": "FK_AddMyVariantFK_Variant",
                      "Columns": "col2",
                      "RelatedTable": "AddMyVariantFK",
                      "RelatedColumns": "Id",
                      "ShouldApplyExpression": "false"
                    }
                ]
            },
            {
                "Name": "AddMyVariantCheck",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": true }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_AddMyVariantCheck_Variant",
                      "Expression": "`col1` > 0",
                      "ShouldApplyExpression": "true"
                    },
                    {
                      "Name": "CHK_AddMyVariantCheck_Variant",
                      "Expression": "`col1` < 0",
                      "ShouldApplyExpression": "false"
                    }
                ]
            },
            {
                "Name": "AddMyMultipleItems",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Column1", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "Column2", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "IDX_MultiColumn1", "IndexColumns": "Column1" },
                    { "Name": "IDX_MultiColumn2", "IndexColumns": "Column2" }
                ]
            },
            {
                "Name": "AddMyMultipleItemsBootstrap",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Column1", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "Column2", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "IDX_BootstrapColumn1", "IndexColumns": "Column1" },
                    { "Name": "IDX_BootstrapColumn2", "IndexColumns": "Column2" }
                ]
            }
            ]
            """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        cmd.ExecuteNonQuery();

        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ForeignKeyQuench('{_productName}', '{TestSchema}', 0, 0, 1)";
        cmd.ExecuteNonQuery();

        // Index Only
        var jsonIO = """
            [
            {
                "Name": "AddMyIndexIO",
                "Indexes": [
                    { "Name": "IDX_NewIndexIO", "IndexColumns": "Id" }
                ]
            }
            ]
            """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{jsonIO.Replace("'", "''")}')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_IndexOnlyQuench('{_productName}', '{TestSchema}', 0, 0, 1)";
        cmd.ExecuteNonQuery();

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

    // An index the package declares but a ShouldApplyExpression turns off was dropped as "unknown", which reads as an
    // index nobody declared.
    [Test]
    public void ADeclaredIndexGatedOff_IsDroppedAsNotDeclaredForThisDeploy_NotAsUnknown()
    {
        var table = $"gated_{Guid.NewGuid():N}"[..14];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(string gate) => $$"""
            [{ "Name": "{{table}}",
               "Columns": [ { "Name": "id", "DataType": "INT", "Nullable": false }, { "Name": "region", "DataType": "INT", "Nullable": true } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                            { "Name": "ix_region", "IndexColumns": "region"{{gate}} } ] }]
            """;
        try
        {
            RunTableQuenchProc(cmd, Json(""));
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(", \"ShouldApplyExpression\": \"0\""));

            cmd.CommandText = $"SELECT COALESCE(GROUP_CONCAT(Message SEPARATOR ' | '), '') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%ix_region%'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo($"  Drop index not declared for this deploy: {table}.ix_region"));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{table}%'";
            cmd.ExecuteNonQuery();
        }
    }
}
