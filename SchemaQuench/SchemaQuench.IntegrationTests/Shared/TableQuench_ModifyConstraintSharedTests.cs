// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;
using Schema.IntegrationTests;

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
        var referenced = cmd.ExecuteScalar()?.ToString();
        Assert.That(referenced, CatalogName.Matches(conn, "ModifyFKRefTblRefNew"));

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
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below,
        // which keeps DropCheckConstraintsRemovedFromProduct as its 4th and last argument.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ForeignKeyQuench('{_productName}', '{TestSchema}', 0, 0, 1)";
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


    // A key the package does not declare -- here on a table it does not declare at all -- was dropped for the column change
    // and never put back, since the foreign-key phase re-adds only declared keys. The deploy now stops, naming it.
    [Test]
    public void TableQuench_ColumnChangeBlockedByAnUndeclaredForeignKey_StopsNamingIt_AndDropsNothing()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        var schema = CreatePrivateDdlDatabase(cmd, "undecl");
        var product = $"UndeclFkProduct_{Guid.NewGuid():N}"[..24];
        const string parent = "undecl_fk_parent";
        const string stranger = "undecl_fk_stranger";
        var json = $$"""
        [ { "Name": "{{parent}}",
            "Columns": [ { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": false, "Collation": "utf8mb4_unicode_ci" } ],
            "Indexes": [ { "Name": "PK_{{parent}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Code" } ] } ]
        """;
        try
        {
            cmd.CommandText = $@"CREATE TABLE `{schema}`.`{parent}` (Code VARCHAR(20) COLLATE utf8mb4_general_ci NOT NULL, CONSTRAINT PK_{parent} PRIMARY KEY (Code))";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $@"CREATE TABLE `{schema}`.`{stranger}` (Id INT NOT NULL PRIMARY KEY, Code VARCHAR(20) COLLATE utf8mb4_general_ci NOT NULL,
                                  CONSTRAINT FK_{stranger} FOREIGN KEY (Code) REFERENCES `{schema}`.`{parent}`(Code))";
            cmd.ExecuteNonQuery();

            var ex = Assert.Catch(() => RunFullQuench(cmd, json, product, schema));
            Assert.That(ex!.Message, Does.Contain($"FK_{stranger}"));

            cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{schema}' AND CONSTRAINT_NAME = 'FK_{stranger}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the key is still there");
        }
        finally
        {
            DropPrivateDdlDatabase(cmd, schema, product);
        }
        conn.Close();
    }

    // MySQL, and MariaDB before 10.4.31, 10.5.22, 10.6.15, 10.11.5, 11.0.3 and 11.1.2, widen a VARCHAR or VARBINARY in place
    // under a foreign key and keep the key, while the length stays on the same side of 255 bytes. The deploy counted every
    // type change as blocking, so a widening 2.7 made refused under a key the package does not declare. Whether the engine
    // makes the change is asked of the engine, here, rather than of the gate under test.
    private bool EngineWidensUnderAForeignKey(System.Data.IDbCommand cmd, string schema)
    {
        cmd.CommandText = $@"CREATE TABLE `{schema}`.`probe_p` (Code VARCHAR(10) NOT NULL, UNIQUE KEY uq (Code));
                             CREATE TABLE `{schema}`.`probe_c` (Code VARCHAR(10), CONSTRAINT fk_probe FOREIGN KEY (Code) REFERENCES `{schema}`.`probe_p`(Code))";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $"ALTER TABLE `{schema}`.`probe_p` MODIFY Code VARCHAR(20) NOT NULL";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (MySqlConnector.MySqlException e) when (e.Number is 1832 or 1833)
        {
            return false;
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{schema}`.`probe_c`; DROP TABLE IF EXISTS `{schema}`.`probe_p`";
            cmd.ExecuteNonQuery();
        }
    }

    private static string AccountJson(string length) => $$"""
        [ { "Name": "fkw_account",
            "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false },
                         { "Name": "Code", "DataType": "VARCHAR({{length}})", "Nullable": false } ],
            "Indexes": [ { "Name": "PK_fkw_account", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" },
                         { "Name": "uq_fkw_account_code", "Unique": true, "IndexColumns": "Code" } ] } ]
        """;

    private void SeedAccountAndUndeclaredPayment(System.Data.IDbCommand cmd, string schema)
    {
        cmd.CommandText = $@"CREATE TABLE `{schema}`.`fkw_account` (Id INT NOT NULL, Code VARCHAR(10) CHARACTER SET utf8mb4 NOT NULL,
                                CONSTRAINT PK_fkw_account PRIMARY KEY (Id), UNIQUE KEY uq_fkw_account_code (Code));
                             CREATE TABLE `{schema}`.`fkw_payment` (Id INT NOT NULL PRIMARY KEY, Code VARCHAR(10) CHARACTER SET utf8mb4 NOT NULL,
                                CONSTRAINT fk_fkw_payment_account FOREIGN KEY (Code) REFERENCES `{schema}`.`fkw_account`(Code));
                             INSERT INTO `{schema}`.`fkw_account` VALUES (1, 'A-1');
                             INSERT INTO `{schema}`.`fkw_payment` VALUES (1, 'A-1')";
        cmd.ExecuteNonQuery();
    }

    private static string CodeType(System.Data.IDbCommand cmd, string schema)
    {
        cmd.CommandText = $"SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = 'fkw_account' AND COLUMN_NAME = 'Code'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    private static int KeyCount(System.Data.IDbCommand cmd, string schema)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{schema}' AND CONSTRAINT_NAME = 'fk_fkw_payment_account'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Test]
    public void TableQuench_VarcharWideningUnderAnUndeclaredForeignKey_AppliesWhereTheEngineKeepsTheKey_AndStopsWhereItCannot()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        var schema = CreatePrivateDdlDatabase(cmd, "fkwiden");
        var product = $"FkWidenProduct_{Guid.NewGuid():N}"[..24];
        try
        {
            var engineWidens = EngineWidensUnderAForeignKey(cmd, schema);
            SeedAccountAndUndeclaredPayment(cmd, schema);

            if (engineWidens)
            {
                Assert.DoesNotThrow(() => RunFullQuench(cmd, AccountJson("20"), product, schema), "the engine widens the column under the key");
                Assert.Multiple(() =>
                {
                    Assert.That(CodeType(cmd, schema), Is.EqualTo("varchar(20)"), "the declared length is applied");
                    Assert.That(KeyCount(cmd, schema), Is.EqualTo(1), "and the key the package does not declare is still there");
                });
            }
            else
            {
                var ex = Assert.Catch(() => RunFullQuench(cmd, AccountJson("20"), product, schema));
                Assert.That(ex!.Message, Does.Contain("fk_fkw_payment_account").And.Contain("is not in the package"),
                    "this engine cannot widen under the key, so the deploy stops and names it, rather than leaving the engine to refuse");
                Assert.Multiple(() =>
                {
                    Assert.That(CodeType(cmd, schema), Is.EqualTo("varchar(10)"), "nothing was changed");
                    Assert.That(KeyCount(cmd, schema), Is.EqualTo(1), "and the key is still there");
                });
            }
        }
        finally
        {
            DropPrivateDdlDatabase(cmd, schema, product);
        }
        conn.Close();
    }

    // Past 255 bytes the length prefix grows to two bytes, which no engine does in place under a key: utf8mb4's VARCHAR(64)
    // is 256 bytes (the seeded columns name utf8mb4, since MySQL 5.7 and MariaDB 10.2 default to latin1, where it is 64).
    // Still refused, naming the key, on every engine.
    [Test]
    public void TableQuench_VarcharWideningPast255BytesUnderAnUndeclaredForeignKey_StopsNamingIt()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        var schema = CreatePrivateDdlDatabase(cmd, "fkwide255");
        var product = $"FkWide255Product_{Guid.NewGuid():N}"[..24];
        try
        {
            SeedAccountAndUndeclaredPayment(cmd, schema);

            var ex = Assert.Catch(() => RunFullQuench(cmd, AccountJson("64"), product, schema));
            Assert.That(ex!.Message, Does.Contain("fk_fkw_payment_account").And.Contain("is not in the package"));
            Assert.Multiple(() =>
            {
                Assert.That(CodeType(cmd, schema), Is.EqualTo("varchar(10)"), "nothing was changed");
                Assert.That(KeyCount(cmd, schema), Is.EqualTo(1), "and the key is still there");
            });
        }
        finally
        {
            DropPrivateDdlDatabase(cmd, schema, product);
        }
        conn.Close();
    }

    // Dropping and re-adding a declared key re-validates every row of the referencing table, so where the engine widens
    // the column in place and keeps the key, the key is left in place.
    [Test]
    public void TableQuench_VarcharWideningUnderADeclaredForeignKey_LeavesTheKeyInPlace_WhereTheEngineKeepsIt()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        var schema = CreatePrivateDdlDatabase(cmd, "fkwdecl");
        var product = $"FkWideDeclProduct_{Guid.NewGuid():N}"[..24];
        try
        {
            if (!EngineWidensUnderAForeignKey(cmd, schema))
                Assert.Ignore("This engine cannot widen a column under a foreign key, so the key must be dropped first.");
            SeedAccountAndUndeclaredPayment(cmd, schema);
            var json = AccountJson("20").TrimEnd().TrimEnd(']') + """
                , { "Name": "fkw_payment",
                    "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Code", "DataType": "VARCHAR(10)", "Nullable": false } ],
                    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" },
                                 { "Name": "fk_fkw_payment_account", "IndexColumns": "Code" } ],
                    "ForeignKeys": [ { "Name": "fk_fkw_payment_account", "Columns": "Code", "RelatedTable": "fkw_account", "RelatedColumns": "Code" } ] } ]
                """;

            RunFullQuench(cmd, json, product, schema);

            cmd.CommandText = "SELECT COALESCE(GROUP_CONCAT(Message SEPARATOR ' | '), '') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            var messages = Convert.ToString(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(CodeType(cmd, schema), Is.EqualTo("varchar(20)"));
                Assert.That(KeyCount(cmd, schema), Is.EqualTo(1));
                Assert.That(messages, Does.Not.Contain("Drop FK for column collation change"), "the key was not dropped to make the change");
            });
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
    /// fix it -- the real defect is two concurrent DDL streams in one database, so each gets
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
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{schema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{product}', '{schema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{product}', '{schema}', 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ForeignKeyQuench('{product}', '{schema}', 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

}
