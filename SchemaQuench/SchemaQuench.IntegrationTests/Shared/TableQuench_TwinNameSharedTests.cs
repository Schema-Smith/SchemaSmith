// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Names the engine keeps apart must stay apart. <c>resume</c> and <c>résumé</c> are two columns, two indexes and two
/// tables on every supported MySQL and MariaDB version, and with <c>lower_case_table_names = 0</c> so are the tables
/// <c>TwinCase</c> and <c>twincase</c>. A comparison under a case- or accent-insensitive collation pairs each twin with
/// the other's definition.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_TwinNameSharedTests : BaseTableQuenchTests
{
    [Test]
    public void AccentTwinColumnsAndIndexes_DeployOnce_AndKeepTheirOwnDefinitions()
    {
        const string table = "TwinCols";
        const string product = "Twin Columns";
        var json = $$"""
            [{
                "Name": "{{table}}",
                "Columns": [
                    { "Name": "id", "DataType": "INT", "Nullable": false },
                    { "Name": "resume", "DataType": "INT", "Nullable": true },
                    { "Name": "résumé", "DataType": "VARCHAR(20)", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                    { "Name": "ix_resume", "IndexColumns": "resume" },
                    { "Name": "ix_résumé", "Unique": true, "IndexColumns": "résumé" }
                ]
            }]
            """;

        using var conn = Open(out var cmd);
        CleanUp(cmd, product, table);
        try
        {
            RunTableQuenchProc(cmd, json, productName: product);
            Exec(cmd, $"INSERT INTO `{table}` VALUES (1, 7, 'cv')");

            ClearStatusMessages(cmd);
            RunTableQuenchProc(cmd, json, productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT CONCAT(`resume`, '|', `résumé`) FROM `{table}`"), Is.EqualTo("7|cv"));
                Assert.That(ColumnType(cmd, table, "resume"), Is.EqualTo("int"));
                Assert.That(ColumnType(cmd, table, "résumé"), Is.EqualTo("varchar(20)"));
                Assert.That(IndexShape(cmd, table, "ix_resume"), Is.EqualTo("1:resume"));
                Assert.That(IndexShape(cmd, table, "ix_résumé"), Is.EqualTo("0:résumé"));
                Assert.That(Messages(cmd), Is.Empty, "a redeploy of an unchanged package changes nothing");
            });
        }
        finally
        {
            CleanUp(cmd, product, table);
        }
    }

    [Test]
    public void AccentTwinTables_DeployOnce_AndDropByAbsenceTakesOnlyTheTwinThatLeft()
        => TwinTablesKeepApart("twin_resume", "twin_résumé", "Twin Accent Tables");

    [Test]
    public void CaseTwinTables_DeployOnce_AndDropByAbsenceTakesOnlyTheTwinThatLeft()
    {
        using (var conn = Open(out var cmd))
            if (Scalar(cmd, "SELECT @@lower_case_table_names") != "0")
                Assert.Ignore("Tables differing only in case are one table when the server folds table names.");
        TwinTablesKeepApart("TwinCase", "twincase", "Twin Case Tables");
    }

    [Test]
    public void RenamingOneAccentTwinTable_LeavesTheOtherOwned()
    {
        const string product = "Twin Rename";
        const string kept = "twrn_resume", renamed = "twrn_résumé", renamedTo = "twrn_cv";
        using var conn = Open(out var cmd);
        CleanUp(cmd, product, kept, renamed, renamedTo);
        try
        {
            RunTableQuenchProc(cmd, $"[{Table(kept, "INT")}, {Table(renamed, "VARCHAR(20)")}]", productName: product);
            RunTableQuenchProc(cmd, $"[{Table(kept, "INT")}, {Table(renamedTo, "VARCHAR(20)", oldName: renamed)}]", productName: product);

            Assert.That(Owned(cmd, product), Is.EqualTo($"{renamedTo},{kept}"),
                "only the renamed twin's ownership moves to the new name");

            RunTableQuenchProc(cmd, $"[{Table(renamedTo, "VARCHAR(20)")}]", productName: product, dropTablesRemovedFromProduct: true);

            Assert.Multiple(() =>
            {
                Assert.That(TableCount(cmd, kept), Is.EqualTo("0"), "the twin left in place is still owned, so its removal drops it");
                Assert.That(TableCount(cmd, renamedTo), Is.EqualTo("1"));
            });
        }
        finally
        {
            CleanUp(cmd, product, kept, renamed, renamedTo);
        }
    }

    [Test]
    public void ExtractingOneAccentTwinTable_ReadsOnlyItsOwnColumnsAndIndexes()
    {
        const string product = "Twin Extract";
        const string plain = "twex_resume", accented = "twex_résumé";
        using var conn = Open(out var cmd);
        CleanUp(cmd, product, plain, accented);
        try
        {
            Exec(cmd, $"CREATE TABLE `{plain}` (`id` INT NOT NULL PRIMARY KEY, `only_plain` INT NULL, `resume` INT NULL, `résumé` INT NULL, "
                      + "KEY `ix_resume` (`resume`), KEY `ix_résumé` (`résumé`))");
            Exec(cmd, $"CREATE TABLE `{accented}` (`id` INT NOT NULL PRIMARY KEY, `only_accented` INT NULL)");

            var json = Scalar(cmd, $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{plain}')");

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("only_plain"));
                Assert.That(json, Does.Not.Contain("only_accented"), json);
                Assert.That(json, Does.Contain("\"ix_resume\"").And.Contain("\"ix_résumé\""), "accent-twin indexes are two indexes");
            });
        }
        finally
        {
            CleanUp(cmd, product, plain, accented);
        }
    }

    private void TwinTablesKeepApart(string first, string second, string product)
    {
        using var conn = Open(out var cmd);
        CleanUp(cmd, product, first, second);
        try
        {
            var both = $"[{Table(first, "INT")}, {Table(second, "VARCHAR(20)")}]";
            RunTableQuenchProc(cmd, both, productName: product);
            Exec(cmd, $"INSERT INTO `{first}` VALUES (1, 7)");
            Exec(cmd, $"INSERT INTO `{second}` VALUES (1, 'cv')");

            ClearStatusMessages(cmd);
            RunTableQuenchProc(cmd, both, productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(ColumnType(cmd, first, "val"), Is.EqualTo("int"));
                Assert.That(ColumnType(cmd, second, "val"), Is.EqualTo("varchar(20)"));
                Assert.That(Messages(cmd), Is.Empty, "a redeploy of an unchanged package changes nothing");
            });

            RunTableQuenchProc(cmd, $"[{Table(first, "INT")}]", productName: product, dropTablesRemovedFromProduct: true);
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT `val` FROM `{first}`"), Is.EqualTo("7"), "the twin still declared keeps its rows");
                Assert.That(TableCount(cmd, second), Is.EqualTo("0"), "the twin removed from the package is dropped");
            });
        }
        finally
        {
            CleanUp(cmd, product, first, second);
        }
    }

    private static string Table(string name, string valType, string oldName = null) => $$"""
        {
            "Name": "{{name}}"{{(oldName == null ? "" : $", \"OldName\": \"{oldName}\"")}},
            "Columns": [
                { "Name": "id", "DataType": "INT", "Nullable": false },
                { "Name": "val", "DataType": "{{valType}}", "Nullable": true }
            ],
            "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ]
        }
        """;

    private string ColumnType(IDbCommand cmd, string table, string column) => Scalar(cmd,
        $"SELECT CONCAT(DATA_TYPE, IFNULL(CONCAT('(', CHARACTER_MAXIMUM_LENGTH, ')'), '')) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' "
        + $"AND BINARY TABLE_NAME = BINARY '{table}' AND BINARY COLUMN_NAME = BINARY '{column}'");

    private string IndexShape(IDbCommand cmd, string table, string index) => Scalar(cmd,
        "SELECT CONCAT(MIN(NON_UNIQUE), ':', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX)) FROM information_schema.STATISTICS "
        + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND BINARY TABLE_NAME = BINARY '{table}' AND BINARY INDEX_NAME = BINARY '{index}'");

    private string TableCount(IDbCommand cmd, string table) => Scalar(cmd,
        $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{_mainDb}' AND BINARY TABLE_NAME = BINARY '{table}'");

    private static string Owned(IDbCommand cmd, string product) => Scalar(cmd,
        "SELECT GROUP_CONCAT(ObjectName ORDER BY BINARY ObjectName) FROM SchemaSmith_ProductOwnership "
        + $"WHERE ProductName = '{product}' AND ObjectType = 'TABLE'");

    private static void ClearStatusMessages(IDbCommand cmd) =>
        Exec(cmd, "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()");

    // Phase headers are logged flush left; the actions a phase takes are indented under it.
    private static string Messages(IDbCommand cmd) => Scalar(cmd,
        "SELECT GROUP_CONCAT(Message SEPARATOR ' / ') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() "
        + "AND Message LIKE '  %'");

    private IDbConnection Open(out IDbCommand cmd)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        return conn;
    }

    private static void CleanUp(IDbCommand cmd, string product, params string[] tables)
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
