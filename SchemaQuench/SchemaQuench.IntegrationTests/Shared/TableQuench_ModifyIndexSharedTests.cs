// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for modifying indexes during table quench.
/// Tests changes to column lists, uniqueness, and index type.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_ModifyIndexSharedTests : BaseTableQuenchTests
{
    private const string TestSchema = "ModifyIndexTests";

    [Test]
    public void ShouldModifyIndexWhenSortColumnListChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Index should now be on Column2 instead of Column1
        cmd.CommandText = $@"
            SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',')
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexColumnList'
              AND INDEX_NAME = 'IDX_ColumnList'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListChangesIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',')
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexColumnListIO'
              AND INDEX_NAME = 'IDX_ColumnListIO'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListOrderChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Index columns should now be Column2,Column1 instead of Column1,Column2
        cmd.CommandText = $@"
            SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',')
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexColumnListOrder'
              AND INDEX_NAME = 'IDX_ColumnList2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2,Column1"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesUnique()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexBecomesUnique'
              AND INDEX_NAME = 'IDX_BecomesUnique'
              AND SEQ_IN_INDEX = 1";
        var nonUnique = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.That(nonUnique, Is.EqualTo(0), "Index should be unique (NON_UNIQUE=0)");

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesNonUnique()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexBecomesNonUnique'
              AND INDEX_NAME = 'IDX_BecomesNonUnique'
              AND SEQ_IN_INDEX = 1";
        var nonUnique = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.That(nonUnique, Is.EqualTo(1), "Index should be non-unique (NON_UNIQUE=1)");

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenDescendingChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // The index must still exist — the descending change/degrade must not drop it.
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexDescending'
              AND INDEX_NAME = 'IDX_Descending'
              AND SEQ_IN_INDEX = 1";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "IDX_Descending should still exist");

        // Check the descending key part. MySQL 8.0+ and MariaDB 10.8+ store DESC key parts (COLLATION='D').
        cmd.CommandText = $@"
            SELECT COLLATION FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_NAME = 'ModifyIndexDescending'
              AND INDEX_NAME = 'IDX_Descending'
              AND SEQ_IN_INDEX = 1";
        var collation = cmd.ExecuteScalar()?.ToString();
        if (TargetSupportsDescendingIndexes(cmd))
        {
            Assert.That(collation, Is.EqualTo("D"), "Descending key part should report COLLATION=D on a target that stores DESC");
        }
        else
        {
            // MySQL 5.7 / MariaDB < 10.8 silently store the key part ascending; SchemaSmith degrades the DESC to
            // ascending idempotently (+ a 'downgraded' manifest row). INFORMATION_SCHEMA reports 'A' — or NULL for
            // an empty-table index on MySQL 5.7 — so assert the key part is NOT descending, not strictly 'A'.
            Assert.That(collation, Is.Not.EqualTo("D"), "Descending key part should be degraded to ascending below the floor");
        }

        conn.Close();
    }

    // MySQL needs an index for a foreign key's columns, so the index the key uses cannot simply be dropped (1553)
    // while a deploy changes it -- whether the package keeps the key or removes it in the same deploy.
    // A new definition that no longer leads with the key's column cannot serve it: the index is kept as it is, the
    // deploy goes on, and a later deploy changes it once the key no longer needs it.
    [TestCase(true, "ParentId,Id", "ParentId,Id", TestName = "ChangingAnIndexAForeignKeyUses_Deploys_KeyKept")]
    [TestCase(false, "ParentId,Id", "ParentId,Id", TestName = "ChangingAnIndexAForeignKeyUses_Deploys_KeyRemoved")]
    [TestCase(true, "Id,ParentId", "ParentId", TestName = "ChangingAnIndexAForeignKeyUses_Deploys_KeyStillNeedsTheOldColumns")]
    public void ChangingAnIndexAForeignKeyUses_Deploys(bool keepForeignKey, string declaredColumns, string expectedColumns)
    {
        var db = $"ModIdxFk_{Guid.NewGuid():N}"[..20];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $@"
CREATE DATABASE `{db}`;
CREATE TABLE `{db}`.`Parent` (`Id` INT NOT NULL PRIMARY KEY);
CREATE TABLE `{db}`.`Child` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NOT NULL,
  INDEX `IX_Child_ParentId` (`ParentId`),
  CONSTRAINT `FK_Child_Parent` FOREIGN KEY (`ParentId`) REFERENCES `{db}`.`Parent` (`Id`));
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{db}', 'TABLE', 'Parent'), ('{_productName}', '', '{db}', 'TABLE', 'Child'),
       ('{_productName}', '', '{db}', 'INDEX', 'Child.IX_Child_ParentId'),
       ('{_productName}', '', '{db}', 'FOREIGN KEY', 'Child.FK_Child_Parent');";
            cmd.ExecuteNonQuery();

            var foreignKeys = keepForeignKey
                ? """[ { "Name": "FK_Child_Parent", "Columns": "ParentId", "RelatedTable": "Parent", "RelatedColumns": "Id" } ]"""
                : "[]";
            var json = $$"""
            [
                { "Name": "Parent", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
                  "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" } ] },
                { "Name": "Child",
                  "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "ParentId", "DataType": "INT", "Nullable": false } ],
                  "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" },
                               { "Name": "IX_Child_ParentId", "IndexColumns": "{{declaredColumns}}" } ],
                  "ForeignKeys": {{foreignKeys}} }
            ]
            """;

            Assert.DoesNotThrow(() =>
            {
                foreach (var call in new[]
                         {
                             $"SchemaSmith_ParseTableJson('{db}', '{json.Replace("'", "''")}')",
                             $"SchemaSmith_MissingTableAndColumnQuench('{db}', 0)",
                             $"SchemaSmith_ModifiedTableQuench('{_productName}', '{db}', 0, 0, 1, 1, 1, 1, 0, 1, 1)",
                             $"SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{db}', 0, 1)",
                             $"SchemaSmith_ForeignKeyQuench('{_productName}', '{db}', 0, 1, 1)"
                         })
                {
                    cmd.CommandText = $"CALL `{_mainDb}`.{call}";
                    cmd.ExecuteNonQuery();
                }
            }, "changing an index a foreign key uses must not stop the deploy");

            cmd.CommandText = $@"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',') FROM INFORMATION_SCHEMA.STATISTICS
                                  WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'Child' AND INDEX_NAME = 'IX_Child_ParentId'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(expectedColumns),
                "the index takes its declared columns when they still serve the key, and is kept as it is when not");
            cmd.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                                  WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'Child' AND CONSTRAINT_NAME = 'FK_Child_Parent'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(keepForeignKey ? 1 : 0),
                keepForeignKey ? "the declared foreign key must still be there" : "the removed foreign key must be gone");
        }
        finally
        {
            cmd.CommandText = $@"DROP DATABASE IF EXISTS `{db}`;
DELETE FROM `{_mainDb}`.SchemaSmith_ProductOwnership WHERE ObjectSchema = '{db}';";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
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
-- ShouldModifyIndexWhenSortColumnListChanges
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyIndexColumnList` (`Column1` INT NOT NULL, `Column2` INT NOT NULL);
CREATE INDEX `IDX_ColumnList` ON `{TestSchema}`.`ModifyIndexColumnList` (`Column1`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyIndexColumnList');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyIndexColumnList.IDX_ColumnList');
-- ShouldModifyIndexWhenSortColumnListOrderChanges
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyIndexColumnListOrder` (`Column1` INT NOT NULL, `Column2` INT NOT NULL);
CREATE INDEX `IDX_ColumnList2` ON `{TestSchema}`.`ModifyIndexColumnListOrder` (`Column1`, `Column2`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyIndexColumnListOrder');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyIndexColumnListOrder.IDX_ColumnList2');
-- ShouldModifyIndexWhenItBecomesUnique
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyIndexBecomesUnique` (`Column1` INT NOT NULL);
CREATE INDEX `IDX_BecomesUnique` ON `{TestSchema}`.`ModifyIndexBecomesUnique` (`Column1`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyIndexBecomesUnique');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyIndexBecomesUnique.IDX_BecomesUnique');
-- ShouldModifyIndexWhenItBecomesNonUnique
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyIndexBecomesNonUnique` (`Column1` INT NOT NULL);
CREATE UNIQUE INDEX `IDX_BecomesNonUnique` ON `{TestSchema}`.`ModifyIndexBecomesNonUnique` (`Column1`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyIndexBecomesNonUnique');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyIndexBecomesNonUnique.IDX_BecomesNonUnique');
-- ShouldModifyIndexWhenDescendingChanges
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyIndexDescending` (`Column1` INT NOT NULL);
CREATE INDEX `IDX_Descending` ON `{TestSchema}`.`ModifyIndexDescending` (`Column1` ASC);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyIndexDescending');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyIndexDescending.IDX_Descending');

-- Index Only Tests
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyIndexColumnListIO` (`Column1` INT NOT NULL, `Column2` INT NOT NULL);
CREATE INDEX `IDX_ColumnListIO` ON `{TestSchema}`.`ModifyIndexColumnListIO` (`Column1`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyIndexColumnListIO');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyIndexColumnListIO.IDX_ColumnListIO');
";
        cmd.ExecuteNonQuery();

        // Run quench with modified index definitions
        var json = """
        [
            {
                "Name": "ModifyIndexColumnList",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false },
                    { "Name": "Column2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_ColumnList", "IndexColumns": "Column2" }
                ]
            },
            {
                "Name": "ModifyIndexColumnListOrder",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false },
                    { "Name": "Column2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_ColumnList2", "IndexColumns": "Column2,Column1" }
                ]
            },
            {
                "Name": "ModifyIndexBecomesUnique",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_BecomesUnique", "IndexColumns": "Column1", "Unique": true }
                ]
            },
            {
                "Name": "ModifyIndexBecomesNonUnique",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_BecomesNonUnique", "IndexColumns": "Column1", "Unique": false },
                    { "Name": "IDX_NewUnique", "IndexColumns": "Column1", "Unique": true }
                ]
            },
            {
                "Name": "ModifyIndexDescending",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_Descending", "IndexColumns": "Column1 DESC" }
                ]
            }
        ]
        """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        cmd.ExecuteNonQuery();

        // Modified-index detection and the DROP half of a modified-index drop-and-recreate live in
        // ModifiedTableQuench; MissingIndexesAndConstraintsQuench does the CREATE half. Both calls are
        // required for this fixture. Trailing 0, 1 are DropUnknownIndexes and
        // DropIndexesRemovedFromProduct, the flags the second call used to carry.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        cmd.ExecuteNonQuery();

        // Index Only test
        var jsonIO = """
        [
            {
                "Name": "ModifyIndexColumnListIO",
                "Indexes": [
                    { "Name": "IDX_ColumnListIO", "IndexColumns": "Column2" }
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
}
