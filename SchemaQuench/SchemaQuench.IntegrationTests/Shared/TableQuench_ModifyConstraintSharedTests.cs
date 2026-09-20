// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for modifying constraints during table quench.
/// Tests default value changes, check constraint changes, and foreign key modifications.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_ModifyConstraintSharedTests : BaseTableQuenchTests
{
    private const string TestSchema = "ModifyConstraintTests";

    [Test]
    public void TableQuench_ShouldModifyDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyMyDefault'
              AND COLUMN_NAME = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyTableLevelCheckConstraint()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Check constraint should exist with new expression
        cmd.CommandText = $@"
            SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = '{TestSchema}'
              AND CONSTRAINT_NAME = 'CHK_ModifyMyTableCheck_MyCheck'";
        var checkClause = cmd.ExecuteScalar()?.ToString();
        Assert.That(checkClause, Does.Contain("100"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForColumnChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // FK should reference Col2 now instead of Col3
        cmd.CommandText = $@"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyFKColumn'
              AND CONSTRAINT_NAME = 'FK_ModifyFKColumn_ModifyFKColumnRef'
              AND REFERENCED_TABLE_NAME IS NOT NULL";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col2"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForReferenceTableChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // FK should now reference ModifyFKRefTblRefNew instead of ModifyFKRefTblRef
        cmd.CommandText = $@"
            SELECT REFERENCED_TABLE_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyFKRefTbl'
              AND CONSTRAINT_NAME = 'FK_ModifyFKRefTbl_Ref'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ModifyFKRefTblRefNew"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForCascadeDeleteChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // FK should no longer have CASCADE delete (changed to NO ACTION)
        cmd.CommandText = $@"
            SELECT DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = '{TestSchema}'
              AND CONSTRAINT_NAME = 'FK_ModFKCascDel_Ref'";
        var deleteRule = cmd.ExecuteScalar()?.ToString();
        Assert.That(deleteRule, Is.EqualTo("NO ACTION").Or.EqualTo("RESTRICT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForCascadeUpdateChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // FK should no longer have CASCADE update (changed to NO ACTION)
        cmd.CommandText = $@"
            SELECT UPDATE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = '{TestSchema}'
              AND CONSTRAINT_NAME = 'FK_ModFKCascUpd_Ref'";
        var updateRule = cmd.ExecuteScalar()?.ToString();
        Assert.That(updateRule, Is.EqualTo("NO ACTION").Or.EqualTo("RESTRICT"));

        conn.Close();
    }

    [OneTimeSetUp]
    public void SetUp()
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
-- TableQuench_ShouldModifyDefault
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyMyDefault` (`Id` INT NOT NULL DEFAULT 10);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyMyDefault');
-- TableQuench_ShouldModifyTableLevelCheckConstraint
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyMyTableCheck` (`Id` INT NOT NULL, `Col2` INT);
ALTER TABLE `{TestSchema}`.`ModifyMyTableCheck` ADD CONSTRAINT `CHK_ModifyMyTableCheck_MyCheck` CHECK (`Id` > 0);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyMyTableCheck');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'CHECK CONSTRAINT', 'ModifyMyTableCheck.CHK_ModifyMyTableCheck_MyCheck');
-- TableQuench_ShouldModifyForeignKeyForColumnChange
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyFKColumn` (`Id` INT NOT NULL PRIMARY KEY, `Col2` INT, `Col3` INT);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyFKColumnRef` (`Id` INT NOT NULL PRIMARY KEY);
ALTER TABLE `{TestSchema}`.`ModifyFKColumn` ADD CONSTRAINT `FK_ModifyFKColumn_ModifyFKColumnRef` FOREIGN KEY (`Col3`) REFERENCES `{TestSchema}`.`ModifyFKColumnRef` (`Id`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyFKColumn');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyFKColumnRef');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'FOREIGN KEY', 'ModifyFKColumn.FK_ModifyFKColumn_ModifyFKColumnRef');
-- TableQuench_ShouldModifyForeignKeyForReferenceTableChange
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyFKRefTbl` (`Id` INT NOT NULL PRIMARY KEY, `Col2` INT, `Col3` INT);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyFKRefTblRef` (`Id` INT NOT NULL PRIMARY KEY);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyFKRefTblRefNew` (`Id` INT NOT NULL PRIMARY KEY);
ALTER TABLE `{TestSchema}`.`ModifyFKRefTbl` ADD CONSTRAINT `FK_ModifyFKRefTbl_Ref` FOREIGN KEY (`Col3`) REFERENCES `{TestSchema}`.`ModifyFKRefTblRef` (`Id`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyFKRefTbl');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyFKRefTblRef');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyFKRefTblRefNew');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'FOREIGN KEY', 'ModifyFKRefTbl.FK_ModifyFKRefTbl_Ref');
-- TableQuench_ShouldModifyForeignKeyForCascadeDeleteChange
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModFKCascDel` (`Id` INT NOT NULL PRIMARY KEY, `Col2` INT, `Col3` INT);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModFKCascDelRef` (`Id` INT NOT NULL PRIMARY KEY);
ALTER TABLE `{TestSchema}`.`ModFKCascDel` ADD CONSTRAINT `FK_ModFKCascDel_Ref` FOREIGN KEY (`Col3`) REFERENCES `{TestSchema}`.`ModFKCascDelRef` (`Id`) ON DELETE CASCADE;
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModFKCascDel');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModFKCascDelRef');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'FOREIGN KEY', 'ModFKCascDel.FK_ModFKCascDel_Ref');
-- TableQuench_ShouldModifyForeignKeyForCascadeUpdateChange
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModFKCascUpd` (`Id` INT NOT NULL PRIMARY KEY, `Col2` INT, `Col3` INT);
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModFKCascUpdRef` (`Id` INT NOT NULL PRIMARY KEY);
ALTER TABLE `{TestSchema}`.`ModFKCascUpd` ADD CONSTRAINT `FK_ModFKCascUpd_Ref` FOREIGN KEY (`Col3`) REFERENCES `{TestSchema}`.`ModFKCascUpdRef` (`Id`) ON UPDATE CASCADE;
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModFKCascUpd');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModFKCascUpdRef');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'FOREIGN KEY', 'ModFKCascUpd.FK_ModFKCascUpd_Ref');
";
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Name": "ModifyMyDefault",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false, "Default": "0" }
                ]
            },
            {
                "Name": "ModifyMyTableCheck",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true }
                ],
                "CheckConstraints": [
                    { "Name": "CHK_ModifyMyTableCheck_MyCheck", "Expression": "`Id` > 100" }
                ]
            },
            {
                "Name": "ModifyFKColumn",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true },
                    { "Name": "Col3", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" }
                ],
                "ForeignKeys": [
                    {
                        "Name": "FK_ModifyFKColumn_ModifyFKColumnRef",
                        "Columns": "Col2",
                        "RelatedTable": "ModifyFKColumnRef",
                        "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Name": "ModifyFKRefTbl",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true },
                    { "Name": "Col3", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" }
                ],
                "ForeignKeys": [
                    {
                        "Name": "FK_ModifyFKRefTbl_Ref",
                        "Columns": "Col3",
                        "RelatedTable": "ModifyFKRefTblRefNew",
                        "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Name": "ModFKCascDel",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true },
                    { "Name": "Col3", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" }
                ],
                "ForeignKeys": [
                    {
                        "Name": "FK_ModFKCascDel_Ref",
                        "Columns": "Col2",
                        "RelatedTable": "ModFKCascDelRef",
                        "RelatedColumns": "Id",
                        "DeleteAction": "NO ACTION"
                    }
                ]
            },
            {
                "Name": "ModFKCascUpd",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Col2", "DataType": "INT", "Nullable": true },
                    { "Name": "Col3", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" }
                ],
                "ForeignKeys": [
                    {
                        "Name": "FK_ModFKCascUpd_Ref",
                        "Columns": "Col2",
                        "RelatedTable": "ModFKCascUpdRef",
                        "RelatedColumns": "Id",
                        "UpdateAction": "NO ACTION"
                    }
                ]
            }
        ]
        """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        cmd.ExecuteNonQuery();

        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below,
        // which keeps DropCheckConstraintsRemovedFromProduct as its 4th and last argument.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ForeignKeyQuench('{_productName}', '{TestSchema}', 0, 0, 1)";
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
    // CONVERT TO CHARACTER SET rewrites every character column on the table, and MySQL refuses outright
    // while a foreign key references one (error 3780, "Referencing column ... are incompatible"). That is a
    // hard deploy failure, not churn. Note the direction: converting the REFERENCED table is what fails, so
    // the FK that has to go is the one on the child.
    [Test]
    public void TableQuench_CollationChange_DropsDependentForeignKeysAndRestoresThem()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Its own database: this test runs DDL, and a sibling test's concurrent ALTER in a shared
        // database makes the engine refuse this one's catalog reads outright. Its own product name too --
        // FK reconciliation is ownership-scoped, so sharing the fixture product would let this package's
        // two tables look like the whole product and strip sibling tests' foreign keys.
        var schema = CreatePrivateDdlDatabase(cmd, "coll");
        var product = $"CollFkProduct_{Guid.NewGuid():N}"[..24];
        var parent = "coll_fk_parent";
        var child = "coll_fk_child";
        var json = $$"""
        [
            {
                "Name": "{{parent}}",
                "Collation": "utf8mb4_unicode_ci",
                "Columns": [ { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": false } ],
                "Indexes": [ { "Name": "PK_{{parent}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Code" } ]
            },
            {
                "Name": "{{child}}",
                "Collation": "utf8mb4_unicode_ci",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": false }
                ],
                "Indexes": [ { "Name": "PK_{{child}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
                "ForeignKeys": [ { "Name": "FK_{{child}}_{{parent}}", "Columns": "Code",
                                    "RelatedTable": "{{parent}}", "RelatedColumns": "Code" } ]
            }
        ]
        """;

        try
        {
            // Live tables start on a DIFFERENT collation, so the deploy must convert them.
            cmd.CommandText = $@"CREATE TABLE `{schema}`.`{parent}` (Code VARCHAR(20) NOT NULL,
                                  CONSTRAINT PK_{parent} PRIMARY KEY (Code)) COLLATE=utf8mb4_general_ci";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $@"CREATE TABLE `{schema}`.`{child}` (Id INT NOT NULL, Code VARCHAR(20) NOT NULL,
                                  CONSTRAINT PK_{child} PRIMARY KEY (Id),
                                  CONSTRAINT FK_{child}_{parent} FOREIGN KEY (Code)
                                    REFERENCES `{schema}`.`{parent}`(Code)) COLLATE=utf8mb4_general_ci";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunFullQuench(cmd, json, product, schema),
                "a collation change must drop the dependent foreign key rather than let the engine refuse");

            cmd.CommandText = $@"SELECT TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES
                                  WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{parent}'";
            Assert.That(Convert.ToString(cmd.ExecuteScalar()), Is.EqualTo("utf8mb4_unicode_ci"),
                "the declared collation must actually be applied");

            cmd.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                                  WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{child}'
                                    AND REFERENCED_TABLE_NAME = '{parent}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                "the foreign key must be put back by the foreign-key phase, not left dropped");
        }
        finally
        {
            DropPrivateDdlDatabase(cmd, schema, product);
        }
        conn.Close();
    }

    // The COLUMN-level twin of the test above, and a genuinely different code path. That one converts the
    // whole table (CONVERT TO CHARACTER SET); this one changes one column's collation, which emits a
    // per-column MODIFY COLUMN ... COLLATE. The engine refuses that too while a foreign key references the
    // column ("Cannot change column ...: used in a foreign key constraint"), but the FK drop was collected
    // only for tables whose TABLE collation differed — so a package that declares a column collation the
    // target does not have failed to deploy. That is the ordinary case when a package moves between servers
    // with different defaults. Note the table declares NO collation here, so the table-level conversion
    // path cannot fire and this proves the column path on its own.
    [Test]
    public void TableQuench_ColumnCollationChange_DropsDependentForeignKeysAndRestoresThem()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Its own database: this test runs DDL, and a sibling test's concurrent ALTER in a shared
        // database makes the engine refuse this one's catalog reads outright. Its own product name too --
        // FK reconciliation is ownership-scoped, so sharing the fixture product would let this package's
        // two tables look like the whole product and strip sibling tests' foreign keys.
        var schema = CreatePrivateDdlDatabase(cmd, "colcoll");
        var product = $"ColCollFkProduct_{Guid.NewGuid():N}"[..24];
        var parent = "colcoll_fk_parent";
        var child = "colcoll_fk_child";
        var json = $$"""
        [
            {
                "Name": "{{parent}}",
                "Columns": [ { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": false, "Collation": "utf8mb4_unicode_ci" } ],
                "Indexes": [ { "Name": "PK_{{parent}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Code" } ]
            },
            {
                "Name": "{{child}}",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": false, "Collation": "utf8mb4_unicode_ci" }
                ],
                "Indexes": [ { "Name": "PK_{{child}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
                "ForeignKeys": [ { "Name": "FK_{{child}}_{{parent}}", "Columns": "Code",
                                    "RelatedTable": "{{parent}}", "RelatedColumns": "Code" } ]
            }
        ]
        """;

        try
        {
            // Live COLUMNS start on a different collation; the tables themselves are left alone, so only the
            // per-column MODIFY COLUMN path can be responsible for what happens next.
            cmd.CommandText = $@"CREATE TABLE `{schema}`.`{parent}` (Code VARCHAR(20) COLLATE utf8mb4_general_ci NOT NULL,
                                  CONSTRAINT PK_{parent} PRIMARY KEY (Code))";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $@"CREATE TABLE `{schema}`.`{child}` (Id INT NOT NULL, Code VARCHAR(20) COLLATE utf8mb4_general_ci NOT NULL,
                                  CONSTRAINT PK_{child} PRIMARY KEY (Id),
                                  CONSTRAINT FK_{child}_{parent} FOREIGN KEY (Code)
                                    REFERENCES `{schema}`.`{parent}`(Code))";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunFullQuench(cmd, json, product, schema),
                "a column-level collation change must drop the dependent foreign key rather than let the engine refuse");

            cmd.CommandText = $@"SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS
                                  WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{parent}' AND COLUMN_NAME = 'Code'";
            Assert.That(Convert.ToString(cmd.ExecuteScalar()), Is.EqualTo("utf8mb4_unicode_ci"),
                "the declared column collation must actually be applied");

            cmd.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                                  WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{child}'
                                    AND REFERENCED_TABLE_NAME = '{parent}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                "the foreign key must be put back by the foreign-key phase, not left dropped");
        }
        finally
        {
            DropPrivateDdlDatabase(cmd, schema, product);
        }
        conn.Close();
    }


    /// <summary>
    /// A private target database for a test that runs DDL.
    /// <para>The fixture's six read-only tests happily share <c>TestSchema</c>, but the two collation
    /// tests each issue ALTER TABLE, and the engine's catalog reads are database-wide rather than
    /// scoped to the declared tables. Run concurrently, one test's quench reads INFORMATION_SCHEMA
    /// while the other's ALTER is in flight and the server refuses the read outright:</para>
    /// <code>Table 'ModifyConstraintTests'.'colcoll_fk_child' was skipped since its definition
    /// is being modified by concurrent DDL statement</code>
    /// <para>Reproduced at <c>NUnit.NumberOfTestWorkers=2</c>; it is one of the two races that forced
    /// the assembly to <c>LevelOfParallelism(1)</c>. Serialising the tests would hide it rather than
    /// fix it (Rule 28) -- the real defect is two concurrent DDL streams in one database, so each gets
    /// its own. This is cheap: the target database is never kindled (the SchemaSmith procedures live in
    /// the suite database and take the target as a parameter), so this is one CREATE DATABASE, not a
    /// kindle and a plan compile.</para>
    /// </summary>
    private string CreatePrivateDdlDatabase(System.Data.IDbCommand cmd, string tag)
    {
        var schema = $"{TestSchema}_{tag}_{Guid.NewGuid():N}"[..40];
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{schema}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE DATABASE `{schema}`";
        cmd.ExecuteNonQuery();
        return schema;
    }

    /// <summary>
    /// Drops the private database AND the ownership rows that pointed into it. Those rows live in the
    /// suite database, so dropping the target alone would leave them behind forever: the product name is
    /// a fresh GUID each run, so no later quench ever names it again and the catalog-reconcile prune --
    /// which is product-scoped -- can never reclaim them.
    /// </summary>
    private void DropPrivateDdlDatabase(System.Data.IDbCommand cmd, string schema, string product)
    {
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{schema}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DELETE FROM `{_mainDb}`.SchemaSmith_ProductOwnership WHERE ProductName = '{product}'";
        cmd.ExecuteNonQuery();
    }

    private void RunFullQuench(System.Data.IDbCommand cmd, string json, string product, string schema)
    {
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{schema}', '{json.Replace("'", "''")}')";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{schema}', 0)";
        cmd.ExecuteNonQuery();
        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{product}', '{schema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{product}', '{schema}', 0, 1)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ForeignKeyQuench('{product}', '{schema}', 0, 0, 1)";
        cmd.ExecuteNonQuery();
    }

}
