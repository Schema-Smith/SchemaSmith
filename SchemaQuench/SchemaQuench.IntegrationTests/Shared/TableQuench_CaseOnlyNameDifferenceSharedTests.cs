// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A package that spells a live column, index, CHECK or foreign key differently ONLY in case names the same object:
/// the engine treats these identifiers case-insensitively on every <c>lower_case_table_names</c> setting.
/// <para>The quench compared them with <c>BINARY</c>, so the live <c>CustomerName</c> looked absent from a package
/// declaring <c>customername</c>: drop-by-absence dropped it with its data, the add path then found it "present" in its
/// earlier snapshot and skipped it, and the deploy exited 0 with the column gone. Indexes and constraints failed the
/// other way, with a duplicate-name error on the re-create.</para>
/// <para>The package is the source of truth, so the live object takes the package spelling. The survival assertions
/// come first: a converge that loses the data is the defect itself.</para>
/// </summary>
[Category("Integration")]
public abstract class TableQuench_CaseOnlyNameDifferenceSharedTests : BaseTableQuenchTests
{
    [Test]
    public void ACaseOnlyColumnSpelling_KeepsTheColumnAndItsRows_AndTakesThePackageSpelling()
    {
        const string table = "CaseOnlyCol";
        const string product = "Case Only Column";
        var json = $$"""
            [{
                "Name": "{{table}}",
                "Columns": [
                    { "Name": "id", "DataType": "INT", "Nullable": false },
                    { "Name": "customername", "DataType": "VARCHAR(50)", "Nullable": true }
                ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ]
            }]
            """;

        using var conn = Open(out var cmd);
        CleanUp(cmd, product, table);
        Exec(cmd, $"CREATE TABLE `{table}` (`id` INT NOT NULL PRIMARY KEY, `CustomerName` VARCHAR(50) NULL)");
        Exec(cmd, $"INSERT INTO `{table}` VALUES (1, 'first'), (2, 'second')");

        try
        {
            RunTableQuenchProc(cmd, json, productName: product);
            AssertColumnSurvived(cmd, table, "after the first deploy");

            RunTableQuenchProc(cmd, json, productName: product);
            AssertColumnSurvived(cmd, table, "after a redeploy");
        }
        finally
        {
            CleanUp(cmd, product, table);
        }
    }

    private void AssertColumnSurvived(IDbCommand cmd, string table, string when)
    {
        Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(LOWER(COLUMN_NAME) ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE {TableIs(table)}"),
            Is.EqualTo("id,customername"), $"the column must still exist {when}; it was dropped because its spelling differed only in case");
        Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(COALESCE(customername, '<null>') ORDER BY id) FROM `{table}`"),
            Is.EqualTo("first,second"), $"the column's rows must survive {when}");
        Assert.That(Scalar(cmd, $"SELECT CAST(COLUMN_NAME AS BINARY) FROM INFORMATION_SCHEMA.COLUMNS WHERE {TableIs(table)} AND LOWER(COLUMN_NAME) = 'customername'"),
            Is.EqualTo("customername"), $"the live column must take the package's spelling {when}");
    }

    [Test]
    public void ACaseOnlyIndexSpelling_DeploysAndTakesThePackageSpelling()
    {
        const string table = "CaseOnlyIdx";
        const string product = "Case Only Index";
        var json = $$"""
            [{
                "Name": "{{table}}",
                "Columns": [
                    { "Name": "id", "DataType": "INT", "Nullable": false },
                    { "Name": "v", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                    { "Name": "ix_caseonly_v", "IndexColumns": "v" }
                ]
            }]
            """;

        using var conn = Open(out var cmd);
        CleanUp(cmd, product, table);
        Exec(cmd, $"CREATE TABLE `{table}` (`id` INT NOT NULL PRIMARY KEY, `v` INT NULL, KEY `IX_CaseOnly_V` (`v`))");

        var isMariaDb = Scalar(cmd, "SELECT VERSION() LIKE '%MariaDB%'") == "1";
        try
        {
            for (var deploy = 1; deploy <= 2; deploy++)
            {
                Exec(cmd, "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()");
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product),
                    $"deploy {deploy}: an index spelled differently only in case is the same index, not a duplicate to create");
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(DISTINCT CAST(INDEX_NAME AS BINARY)) FROM INFORMATION_SCHEMA.STATISTICS WHERE {TableIs(table)} AND INDEX_NAME <> 'PRIMARY'"),
                    Is.EqualTo("ix_caseonly_v"), $"deploy {deploy}: exactly one secondary index, with the package's spelling");

                // MA-086: a case-only RENAME INDEX corrupts InnoDB's index dictionary on MariaDB versions hit by
                // MDEV-34951, so MariaDB must converge by drop and re-create. MySQL renames, which proves this read
                // can see the rename when one is emitted.
                var renamesLogged = Scalar(cmd, "SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%Rename index (spelling)%'");
                Assert.That(renamesLogged, Is.EqualTo(!isMariaDb && deploy == 1 ? "1" : "0"),
                    $"deploy {deploy}: a case-only rename is emitted only by MySQL, and only once");
            }
        }
        finally
        {
            CleanUp(cmd, product, table);
        }
    }

    [Test]
    public void ACaseOnlyCheckSpelling_DeploysAndTakesThePackageSpelling()
    {
        if (!TestVersionGates.SupportsCheckConstraints(Platform, _connectionString))
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");

        const string table = "CaseOnlyChk";
        const string product = "Case Only Check";
        var json = $$"""
            [{
                "Name": "{{table}}",
                "Columns": [
                    { "Name": "id", "DataType": "INT", "Nullable": false },
                    { "Name": "v", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ],
                "CheckConstraints": [ { "Name": "ck_caseonly_v", "Expression": "v > 0" } ]
            }]
            """;

        using var conn = Open(out var cmd);
        CleanUp(cmd, product, table);
        Exec(cmd, $"CREATE TABLE `{table}` (`id` INT NOT NULL PRIMARY KEY, `v` INT NULL, CONSTRAINT `CK_CaseOnly_V` CHECK (v > 0))");

        try
        {
            for (var deploy = 1; deploy <= 2; deploy++)
            {
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product),
                    $"deploy {deploy}: a CHECK spelled differently only in case is the same constraint, not a duplicate to create");
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(CAST(CONSTRAINT_NAME AS BINARY)) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE {TableIs(table)} AND CONSTRAINT_TYPE = 'CHECK'"),
                    Is.EqualTo("ck_caseonly_v"), $"deploy {deploy}: exactly one CHECK, with the package's spelling");
            }
        }
        finally
        {
            CleanUp(cmd, product, table);
        }
    }

    [Test]
    public void ACaseOnlyForeignKeySpelling_DeploysAndTakesThePackageSpelling()
    {
        const string parent = "CaseOnlyFkParent";
        const string child = "CaseOnlyFkChild";
        const string product = "Case Only Foreign Key";
        var json = $$"""
            [{
                "Name": "{{parent}}",
                "Columns": [ { "Name": "id", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ]
            },
            {
                "Name": "{{child}}",
                "Columns": [
                    { "Name": "id", "DataType": "INT", "Nullable": false },
                    { "Name": "parent_id", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                    { "Name": "ix_caseonlyfk_parent", "IndexColumns": "parent_id" }
                ],
                "ForeignKeys": [ { "Name": "fk_caseonly_parent", "Columns": "parent_id", "RelatedTable": "{{parent}}", "RelatedColumns": "id" } ]
            }]
            """;

        using var conn = Open(out var cmd);
        CleanUp(cmd, product, child, parent);
        Exec(cmd, $"CREATE TABLE `{parent}` (`id` INT NOT NULL PRIMARY KEY)");
        Exec(cmd, $"CREATE TABLE `{child}` (`id` INT NOT NULL PRIMARY KEY, `parent_id` INT NULL, KEY `ix_caseonlyfk_parent` (`parent_id`), " +
                  $"CONSTRAINT `FK_CaseOnly_Parent` FOREIGN KEY (`parent_id`) REFERENCES `{parent}` (`id`))");

        try
        {
            for (var deploy = 1; deploy <= 2; deploy++)
            {
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product),
                    $"deploy {deploy}: a foreign key spelled differently only in case is the same constraint, not a duplicate to create");
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(CAST(CONSTRAINT_NAME AS BINARY)) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE {TableIs(child)} AND CONSTRAINT_TYPE = 'FOREIGN KEY'"),
                    Is.EqualTo("fk_caseonly_parent"), $"deploy {deploy}: exactly one foreign key, with the package's spelling");
            }
        }
        finally
        {
            CleanUp(cmd, product, child, parent);
        }
    }

    /// <summary>
    /// On a server that folds table names the catalog spells this table <c>ownmixchild</c> while the package says
    /// <c>OwnMixChild</c>; ownership must still be recorded for the table and everything on it, or PreventDrop and
    /// drop-by-absence quietly stop applying to it.
    /// </summary>
    [Test]
    public void AMixedCaseTable_RecordsOwnershipForItselfAndItsIndexCheckAndForeignKey()
    {
        const string parent = "OwnMixParent";
        const string child = "OwnMixChild";
        const string product = "Mixed Case Ownership";
        var checks = TestVersionGates.SupportsCheckConstraints(Platform, _connectionString)
            ? """, "CheckConstraints": [ { "Name": "CK_OwnMix_V", "Expression": "V > 0" } ]"""
            : "";
        var json = $$"""
            [{
                "Name": "{{parent}}",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Name": "{{child}}",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "ParentId", "DataType": "INT", "Nullable": true },
                    { "Name": "V", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" },
                    { "Name": "IX_OwnMix_ParentId", "IndexColumns": "ParentId" }
                ],
                "ForeignKeys": [ { "Name": "FK_OwnMix_Parent", "Columns": "ParentId", "RelatedTable": "{{parent}}", "RelatedColumns": "Id" } ]{{checks}}
            }]
            """;

        using var conn = Open(out var cmd);
        CleanUp(cmd, product, child, parent);

        try
        {
            for (var deploy = 1; deploy <= 2; deploy++)
            {
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product), $"deploy {deploy}");

                var owned = Scalar(cmd, $"SELECT GROUP_CONCAT(CONCAT(ObjectType, ':', LOWER(ObjectName)) ORDER BY ObjectType, LOWER(ObjectName) SEPARATOR ' | ') " +
                                        $"FROM SchemaSmith_ProductOwnership WHERE ProductName = '{product}'");
                var expected = (checks == "" ? "" : "CHECK CONSTRAINT:ownmixchild.ck_ownmix_v | ") +
                               "FOREIGN KEY:ownmixchild.fk_ownmix_parent | INDEX:ownmixchild.ix_ownmix_parentid | INDEX:ownmixchild.primary | " +
                               "INDEX:ownmixparent.primary | TABLE:ownmixchild | TABLE:ownmixparent";
                Assert.That(owned, Is.EqualTo(expected), $"deploy {deploy}: every declared object must be recorded as owned");
            }
        }
        finally
        {
            CleanUp(cmd, product, child, parent);
        }
    }

    // LOWER on both sides: on lower_case_table_names >= 1 the catalog holds the folded spelling, and these assertions
    // are about the object inside the table, not about how the server spells the table.
    private string TableIs(string table) =>
        $"LOWER(TABLE_SCHEMA) = LOWER('{_mainDb}') AND LOWER(TABLE_NAME) = LOWER('{table}')";

    private IDbConnection Open(out IDbCommand cmd)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        return conn;
    }

    private void CleanUp(IDbCommand cmd, string product, params string[] tables)
    {
        foreach (var table in tables)
            TryExec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        TryExec(cmd, $"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = '{product}'");
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryExec(IDbCommand cmd, string sql)
    {
        try
        {
            Exec(cmd, sql);
        }
        catch (DbException)
        {
            // Best-effort cleanup: the object may not exist.
        }
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value switch
        {
            null or DBNull => "",
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => value.ToString() ?? ""
        };
    }
}
