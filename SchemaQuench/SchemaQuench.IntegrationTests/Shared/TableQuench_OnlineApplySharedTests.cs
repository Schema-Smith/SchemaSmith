// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// OnlineApply on the MySQL family. Each converted ALTER asks for ALGORITHM=INSTANT, then ALGORITHM=INPLACE, LOCK=NONE,
/// and the engine decides; Prefer ends with the statement as written, Require refuses. A column the deploy adds takes
/// its declared place (AFTER the declared column before it), so the table needs no rebuild to be in order (MY-#8).
/// Expectations follow the probed versions: an instant add anywhere and an instant drop are MySQL 8.0.29 and MariaDB
/// 10.4; below that the same change runs INPLACE, LOCK=NONE.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_OnlineApplySharedTests : BaseTableQuenchTests
{
    private int VersionPatch()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        return VersionHelper.ParsePatchComparable(cmd.ExecuteScalar()?.ToString(), Platform) ?? 0;
    }

    // The form an add anywhere or a drop takes on this server.
    private string InstantOrOnline() => VersionPatch() >= (Platform == Platform.MariaDb ? 100400 : 80029) ? "instant" : "online";

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Sql(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Deploy(IDbCommand cmd, string json, string online, bool whatIf = false, bool indexOnly = false, Action beforeRetry = null)
    {
        Sql(cmd, $"SET @ss_online_apply = '{online}'; DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID(); DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()");
        RunTableQuenchProc(cmd, json, indexOnly: indexOnly, whatIf: whatIf, beforeRetry: beforeRetry);
    }

    // The Mode on this session's latest change row for the object: "<none>" with no row, "NULL" when it carries none.
    private static string Mode(IDbCommand cmd, string objectName, string action) =>
        Scalar(cmd, $"SELECT COALESCE(Mode, 'NULL') FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND REPLACE(ObjectName, '`', '') = '{objectName}' AND ActionType = '{action}' ORDER BY Id DESC LIMIT 1")?.ToString() ?? "<none>";

    private static string Messages(IDbCommand cmd) =>
        Scalar(cmd, "SELECT COALESCE(GROUP_CONCAT(Message ORDER BY Id SEPARATOR '\n'), '') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()")?.ToString() ?? "";

    private string ColumnOrder(IDbCommand cmd, string table) =>
        Scalar(cmd, $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'")?.ToString() ?? "";

    private static string Table(string name, string columns, string extras = "", string tableOnline = null) =>
        $$"""[{ "Name": "{{name}}"{{(tableOnline == null ? "" : $", \"OnlineApply\": \"{tableOnline}\"")}}, "Columns": [{{columns}}]{{extras}} }]""";

    private static string Col(string name, string type = "INT", bool nullable = true) =>
        $$"""{ "Name": "{{name}}", "DataType": "{{type}}", "Nullable": {{(nullable ? "true" : "false")}} }""";

    private void Seed(IDbCommand cmd, string table, string extra = "") =>
        Sql(cmd, $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`; CREATE TABLE `{_mainDb}`.`{table}` (id INT NOT NULL PRIMARY KEY, a INT NULL, b INT NULL{extra}) ENGINE=InnoDB; " +
                 $"INSERT INTO `{_mainDb}`.`{table}` (id, a, b) VALUES (1, 1, 1), (2, 2, 2)");

    private void Drop(IDbCommand cmd, string table) => Sql(cmd, $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`");

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        return conn;
    }

    [Test]
    public void Prefer_AddsAColumnInItsDeclaredPlace()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_add");
            Deploy(cmd, Table("onl_add", string.Join(", ", Col("id", nullable: false), Col("a"), Col("n"), Col("b"))), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ColumnOrder(cmd, "onl_add"), Is.EqualTo("id,a,n,b"), "the column lands where it is declared, not last");
                Assert.That(Mode(cmd, "onl_add.n", "created"), Is.EqualTo(InstantOrOnline()));
                Assert.That(Messages(cmd), Does.Contain("ADD COLUMN").And.Contain("AFTER `a`"));
            });
        }
        finally { Drop(cmd, "onl_add"); }
    }

    [Test]
    public void Prefer_AddsSeveralColumnsInOrder_IncludingFirst()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_add2");
            Deploy(cmd, Table("onl_add2", string.Join(", ", Col("f"), Col("id", nullable: false), Col("a"), Col("n1"), Col("n2"), Col("b"), Col("z"))), "PREFER");
            Assert.That(ColumnOrder(cmd, "onl_add2"), Is.EqualTo("f,id,a,n1,n2,b,z"));
        }
        finally { Drop(cmd, "onl_add2"); }
    }

    [Test]
    public void Off_AppendsTheColumn_AsAlways_AndRecordsNoMode()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_off");
            Deploy(cmd, Table("onl_off", string.Join(", ", Col("id", nullable: false), Col("a"), Col("n"), Col("b"))), "OFF");
            Assert.Multiple(() =>
            {
                Assert.That(ColumnOrder(cmd, "onl_off"), Is.EqualTo("id,a,b,n"));
                Assert.That(Mode(cmd, "onl_off.n", "created"), Is.EqualTo("NULL"));
                Assert.That(Messages(cmd), Does.Not.Contain("ALGORITHM="));
            });
        }
        finally { Drop(cmd, "onl_off"); }
    }

    [TestCase("OFF", "Prefer", true)]
    [TestCase("PREFER", "Off", false)]
    public void ATablesOwnOnlineApply_OverridesTheDeploys(string deployValue, string tableValue, bool online)
    {
        var table = $"onl_ovr_{deployValue.ToLowerInvariant()}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, table);
            Deploy(cmd, Table(table, string.Join(", ", Col("id", nullable: false), Col("a"), Col("n"), Col("b")), tableOnline: tableValue), deployValue);
            Assert.That(Mode(cmd, $"{table}.n", "created"), Is.EqualTo(online ? InstantOrOnline() : "NULL"));
        }
        finally { Drop(cmd, table); }
    }

    [Test]
    public void Prefer_OnATableThisDeployCreates_UsesThePlainStatements()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Drop(cmd, "onl_new");
            // A retry would find the table the failed attempt created, so it starts again from no table.
            Deploy(cmd, Table("onl_new", string.Join(", ", Col("id", nullable: false), Col("a")),
                """, "Indexes": [{ "Name": "ix_onl_new_a", "IndexColumns": "a" }]"""), "PREFER",
                beforeRetry: () => Sql(cmd, $"DROP TABLE IF EXISTS `{_mainDb}`.`onl_new`; DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()"));
            Assert.Multiple(() =>
            {
                Assert.That(IndexExists(cmd, "onl_new", "ix_onl_new_a"), Is.True);
                Assert.That(Mode(cmd, "onl_new.ix_onl_new_a", "created"), Is.EqualTo("NULL"));
            });
        }
        finally { Drop(cmd, "onl_new"); }
    }

    [Test]
    public void Prefer_DropsAColumnThePackageNoLongerDeclares_AndRecordsIt()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_dropc");
            Deploy(cmd, Table("onl_dropc", string.Join(", ", Col("id", nullable: false), Col("a"))), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ColumnExists(cmd, "onl_dropc", "b"), Is.False);
                Assert.That(Mode(cmd, "onl_dropc.b", "dropped"), Is.EqualTo(InstantOrOnline()));
            });
        }
        finally { Drop(cmd, "onl_dropc"); }
    }

    // Before OnlineApply a dropped column wrote no change row at all, so the Deployment Summary never counted one.
    [TestCase(false)]
    [TestCase(true)]
    public void Off_ADroppedColumn_IsInTheChangeAudit(bool whatIf)
    {
        var table = whatIf ? "onl_dropc_wi" : "onl_dropc_off";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, table);
            Deploy(cmd, Table(table, string.Join(", ", Col("id", nullable: false), Col("a"))), "OFF", whatIf: whatIf);
            Assert.Multiple(() =>
            {
                Assert.That(Mode(cmd, $"{table}.b", whatIf ? "wouldDrop" : "dropped"), Is.EqualTo("NULL"));
                Assert.That(ColumnExists(cmd, table, "b"), Is.EqualTo(whatIf));
            });
        }
        finally { Drop(cmd, table); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prefer_BuildsAndDropsIndexesWithoutLockingTheTable(bool indexOnly)
    {
        var table = indexOnly ? "onl_ix_io" : "onl_ix";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, table);
            Sql(cmd, $"CREATE INDEX ix_{table}_b ON `{_mainDb}`.`{table}` (b)");
            Sql(cmd, $"SET @ss_online_apply = 'OFF'");
            // The unknown index goes only when the deploy drops unknown indexes: the index-only path here does, and the
            // full path is given the index as one the product owned, by declaring it first.
            if (!indexOnly)
            {
                RunTableQuenchProc(cmd, Table(table, string.Join(", ", Col("id", nullable: false), Col("a"), Col("b")),
                    $$""", "Indexes": [{ "Name": "ix_{{table}}_b", "IndexColumns": "b" }]"""));
            }
            Deploy(cmd, Table(table, string.Join(", ", Col("id", nullable: false), Col("a"), Col("b")),
                $$""", "Indexes": [{ "Name": "ix_{{table}}_a", "IndexColumns": "a" }]"""), "PREFER", indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(IndexExists(cmd, table, $"ix_{table}_a"), Is.True);
                Assert.That(IndexExists(cmd, table, $"ix_{table}_b"), Is.False);
                Assert.That(Mode(cmd, $"{table}.ix_{table}_a", "created"), Is.EqualTo("online"));
                Assert.That(Mode(cmd, $"{table}.ix_{table}_b", "dropped"), Is.EqualTo("online"));
                Assert.That(Messages(cmd), Does.Contain("(online: ALGORITHM=INPLACE, LOCK=NONE)"));
                // The statement the engine ran last (the create), as sent: the lock clause is what keeps writes going.
                Assert.That(Scalar(cmd, "SELECT @ss_online_sql")?.ToString(), Does.Contain("ALGORITHM=INPLACE").And.Contain("LOCK=NONE"));
            });
        }
        finally { Drop(cmd, table); }
    }

    // A full-text index makes the table refuse INSTANT and INPLACE, LOCK=NONE for a column add (InnoDB rebuilds such a
    // table under a lock). MariaDB 11.2+ still adds it online by copying (COPY, LOCK=NONE).
    [Test]
    public void Prefer_FallsBackToThePlainStatement_WhenTheEngineRefusesBothForms_AndRequireRefuses()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_ft", ", c TEXT NULL, FULLTEXT KEY ft_onl_ft_c (c)");
            var json = Table("onl_ft", string.Join(", ", Col("id", nullable: false), Col("a"), Col("n"), Col("b"), Col("c", "TEXT")),
                """, "FullTextIndexes": [{ "Name": "ft_onl_ft_c", "Columns": "c" }]""");
            if (Platform == Platform.MariaDb && VersionPatch() >= 110200)
            {
                Deploy(cmd, json, "REQUIRE");
                Assert.Multiple(() =>
                {
                    Assert.That(ColumnOrder(cmd, "onl_ft"), Is.EqualTo("id,a,n,b,c"));
                    Assert.That(Mode(cmd, "onl_ft.n", "created"), Is.EqualTo("online"));
                    Assert.That(Messages(cmd), Does.Contain("(online: ALGORITHM=COPY, LOCK=NONE)"));
                });
                return;
            }
            var refusal = Assert.Catch(() => Deploy(cmd, json, "REQUIRE"));
            Assert.Multiple(() =>
            {
                Assert.That(refusal!.Message, Does.Contain("OnlineApply is Require"));
                Assert.That(Messages(cmd), Does.Contain("OnlineApply is Require, and this cannot run online").And.Contain("ADD COLUMN"));
                Assert.That(ColumnExists(cmd, "onl_ft", "n"), Is.False, "nothing was added");
            });

            Deploy(cmd, json, "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ColumnOrder(cmd, "onl_ft"), Is.EqualTo("id,a,n,b,c"), "the plain statement keeps the position");
                Assert.That(Mode(cmd, "onl_ft.n", "created"), Is.EqualTo("unavailable"));
                Assert.That(Messages(cmd), Does.Contain("(online unavailable:"));
            });
        }
        finally { Drop(cmd, "onl_ft"); }
    }

    // MY-#7: each instant add or drop spends one of a table's row versions (64 to MySQL 9.0, 255 from 9.1).
    [Test]
    public void Prefer_WarnsNearTheRowVersionCap_AndRebuildsAtIt()
    {
        if (Platform != Platform.MySQL || VersionPatch() < 80029) Assert.Ignore("Instant row versions are MySQL 8.0.29+.");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_cap");
            var cap = VersionPatch() >= 90100 ? 255 : 64;
            // An instant add or drop spends one version per statement, however many columns it names.
            var pairs = (cap - 6) / 2;
            for (var i = 0; i < pairs; i++)
                Sql(cmd, $"ALTER TABLE `{_mainDb}`.`onl_cap` ADD COLUMN burn INT, ALGORITHM=INSTANT; ALTER TABLE `{_mainDb}`.`onl_cap` DROP COLUMN burn, ALGORITHM=INSTANT");
            var used = 2 * pairs + 1;
            var columns = new System.Collections.Generic.List<string> { Col("id", nullable: false), Col("a"), Col("n1") };
            Deploy(cmd, Table("onl_cap", string.Join(", ", columns) + ", " + Col("b")), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(Mode(cmd, "onl_cap.n1", "created"), Is.EqualTo("instant"));
                Assert.That(Messages(cmd), Does.Contain($"onl_cap has used {used} of its {cap} instant row versions"), "inside the warning band");
            });

            // One column per deploy until the versions run out: each is instant until the cap, and the next rebuilds.
            var instantAdds = 0;
            var form = "instant";
            for (var n = 2; n < 12 && form == "instant"; n++)
            {
                columns.Add(Col($"n{n}"));
                Deploy(cmd, Table("onl_cap", string.Join(", ", columns) + ", " + Col("b")), "PREFER");
                form = Mode(cmd, $"onl_cap.n{n}", "created");
                if (form == "instant") instantAdds++;
            }
            Assert.Multiple(() =>
            {
                Assert.That(instantAdds, Is.EqualTo(cap - used), "every version up to the cap is spent instantly");
                Assert.That(form, Is.EqualTo("online"), "at the cap the add rebuilds, still without locking writes");
                Assert.That(Messages(cmd), Does.Contain($"onl_cap has used all {cap} of its instant row versions"));
                Assert.That(ColumnOrder(cmd, "onl_cap"), Does.EndWith(",b"), "each column still lands in its declared place");
            });
        }
        finally { Drop(cmd, "onl_cap"); }
    }

    private static int AuditCount(IDbCommand cmd, string objectName, string action) =>
        Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND REPLACE(ObjectName, '`', '') = '{objectName}' AND ActionType = '{action}'"));

    private const string OrderPolicy = """, "RebuildPolicy": { "OnOrderMismatch": true }""";

    // MA-057: a table the policy would rebuild only to put its columns in order has them moved in place instead.
    [Test]
    public void Prefer_PutsADriftedTableInOrderInPlace_InsteadOfRebuildingIt()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_ord", ", c VARCHAR(10) NULL");
            Sql(cmd, $"UPDATE `{_mainDb}`.`onl_ord` SET c = CONCAT('c', id)");
            Deploy(cmd, Table("onl_ord", string.Join(", ", Col("id", nullable: false), Col("a"), Col("c", "VARCHAR(10)"), Col("b")), OrderPolicy), "PREFER");
            var expected = Platform == Platform.MariaDb && VersionPatch() >= 100400 ? "instant" : "online";
            Assert.Multiple(() =>
            {
                Assert.That(ColumnOrder(cmd, "onl_ord"), Is.EqualTo("id,a,c,b"));
                Assert.That(AuditCount(cmd, "onl_ord", "rebuilt"), Is.Zero, "moved in place, not rebuilt");
                Assert.That(Mode(cmd, "onl_ord", "modified"), Is.EqualTo(expected));
                Assert.That(Scalar(cmd, $"SELECT CONCAT(c, '|', b) FROM `{_mainDb}`.`onl_ord` WHERE id = 2")?.ToString(), Is.EqualTo("c2|2"), "the values moved with their columns");
            });
            Deploy(cmd, Table("onl_ord", string.Join(", ", Col("id", nullable: false), Col("a"), Col("c", "VARCHAR(10)"), Col("b")), OrderPolicy), "PREFER");
            Assert.That(AuditCount(cmd, "onl_ord", "modified"), Is.Zero, "in order now, so the next deploy has nothing to move");
        }
        finally { Drop(cmd, "onl_ord"); }
    }

    // Below MySQL 8.0.13 an expression default is skipped, but the in-place reorder restated every declared column, the
    // default with it, and the server refused the statement mid-deploy. Such a table now takes the rebuild path, as it
    // does without OnlineApply, which refuses it up front and says why; the rows are untouched.
    [Test]
    public void Prefer_ADriftedTableWithAnExpressionDefaultTheServerCannotHold_IsRefusedAsWithoutOnlineApply()
    {
        if (Platform != Platform.MySQL || VersionPatch() >= 80013) Assert.Ignore("Expression defaults are skipped only below MySQL 8.0.13.");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_exprdef", ", c INT NULL");
            var c = """{ "Name": "c", "DataType": "INT", "Nullable": true, "Default": "(1 + 1)" }""";
            var json = Table("onl_exprdef", string.Join(", ", Col("id", nullable: false), Col("b"), c, Col("a")), OrderPolicy);
            var prefer = Assert.Catch(() => Deploy(cmd, json, "PREFER"));
            var off = Assert.Catch(() => Deploy(cmd, json, "OFF"));
            Assert.Multiple(() =>
            {
                Assert.That(prefer!.Message, Does.Contain("DEFAULT expression requires MySQL 8.0.13"), "refused, naming why, not a syntax error");
                Assert.That(prefer.Message, Is.EqualTo(off!.Message), "the same answer as without OnlineApply");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM `{_mainDb}`.`onl_exprdef`"), Is.EqualTo(2L), "the rows are all there");
            });
        }
        finally { Drop(cmd, "onl_exprdef"); }
    }

    [Test]
    public void Off_StillRebuildsADriftedTable()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_ord_off", ", c INT NULL");
            Deploy(cmd, Table("onl_ord_off", string.Join(", ", Col("id", nullable: false), Col("a"), Col("c"), Col("b")), OrderPolicy), "OFF");
            Assert.Multiple(() =>
            {
                Assert.That(ColumnOrder(cmd, "onl_ord_off"), Is.EqualTo("id,a,c,b"));
                Assert.That(AuditCount(cmd, "onl_ord_off", "rebuilt"), Is.EqualTo(1));
            });
        }
        finally { Drop(cmd, "onl_ord_off"); }
    }

    // A generated column needs more than its declared column script to MODIFY, so its table still rebuilds.
    [Test]
    public void Prefer_ADriftedTableWithAGeneratedColumn_IsStillRebuilt()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_ord_gen", ", c INT NULL, g INT AS (a + 1) VIRTUAL");
            Deploy(cmd, Table("onl_ord_gen", string.Join(", ", Col("id", nullable: false), Col("a"), Col("c"), Col("b"),
                """{ "Name": "g", "DataType": "INT", "Nullable": true, "GenerationExpression": "`a` + 1", "Generated": "VIRTUAL" }"""), OrderPolicy), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ColumnOrder(cmd, "onl_ord_gen"), Is.EqualTo("id,a,c,b,g"));
                Assert.That(AuditCount(cmd, "onl_ord_gen", "rebuilt"), Is.EqualTo(1));
            });
        }
        finally { Drop(cmd, "onl_ord_gen"); }
    }

    [Test]
    public void Prefer_WidensAVarchar_InstantlyOnMariaDb_AndOnlineOnMySql()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_wide", ", s VARCHAR(10) NULL");
            Deploy(cmd, Table("onl_wide", string.Join(", ", Col("id", nullable: false), Col("a"), Col("b"), Col("s", "VARCHAR(40)"))), "PREFER");
            var expected = Platform == Platform.MariaDb && VersionPatch() >= 100300 ? "instant" : "online";
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'onl_wide' AND COLUMN_NAME = 's'"), Is.EqualTo(40L));
                Assert.That(Mode(cmd, "onl_wide.s", "modified"), Is.EqualTo(expected));
            });
        }
        finally { Drop(cmd, "onl_wide"); }
    }

    // A type change copies the table. MariaDB 11.2+ copies without blocking writes (COPY, LOCK=NONE); elsewhere the
    // engine refuses every online form, so Prefer runs the plain statement and Require refuses.
    [Test]
    public void Prefer_ChangesAColumnType_OnlineOnMariaDb112_AndPlainElsewhere_AndRequireRefusesThere()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_type");
            var json = Table("onl_type", string.Join(", ", Col("id", nullable: false), Col("a", "BIGINT"), Col("b")));
            var copyOnline = Platform == Platform.MariaDb && VersionPatch() >= 110200;
            if (!copyOnline)
            {
                var refusal = Assert.Catch(() => Deploy(cmd, json, "REQUIRE"));
                Assert.Multiple(() =>
                {
                    Assert.That(refusal!.Message, Does.StartWith("OnlineApply is Require: onl_type cannot change online").And.EndWith("(the line above says why)"),
                        "the refusal reads whole within the engine's 128-character limit");
                    Assert.That(AuditCount(cmd, "onl_type.a", "modified"), Is.Zero, "a refused change is not reported as made");
                });
            }
            Deploy(cmd, json, "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'onl_type' AND COLUMN_NAME = 'a'")?.ToString(), Is.EqualTo("bigint"));
                Assert.That(Mode(cmd, "onl_type.a", "modified"), Is.EqualTo(copyOnline ? "online" : "unavailable"));
                if (copyOnline) Assert.That(Messages(cmd), Does.Contain("(online: ALGORITHM=COPY, LOCK=NONE)"));
            });
        }
        finally { Drop(cmd, "onl_type"); }
    }

    // A key column's type change drops the foreign key first. Require refused only at the MODIFY, after the drop had
    // committed, so the key was gone and every rerun refused again with it gone.
    [Test]
    public void Require_ATypeChangeOnAForeignKeyColumn_IsRefusedBeforeTheKeyIsDropped()
    {
        if (Platform == Platform.MariaDb && VersionPatch() >= 110200) Assert.Ignore("MariaDB 11.2+ changes the type online.");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        string Keys() => Scalar(cmd, $"SELECT COALESCE(GROUP_CONCAT(CONSTRAINT_NAME), '') FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'onl_fkc'")?.ToString();
        string Package(string type) =>
            $$"""[{ "Name": "onl_fkp", "Columns": [{{Col("id", type, nullable: false)}}], "Indexes": [{ "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "id" }] },""" +
            $$""" { "Name": "onl_fkc", "Columns": [{{Col("id", nullable: false)}}, {{Col("pid", type)}}], "Indexes": [{ "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "id" }, { "Name": "ix_pid", "IndexColumns": "pid" }],""" +
            $$""" "ForeignKeys": [{ "Name": "fk_onl_fkc", "Columns": "pid", "RelatedTable": "onl_fkp", "RelatedColumns": "id" }] }]""";
        try
        {
            Sql(cmd, $"DROP TABLE IF EXISTS `{_mainDb}`.`onl_fkc`; DROP TABLE IF EXISTS `{_mainDb}`.`onl_fkp`");
            Deploy(cmd, Package("INT"), "OFF");
            Assert.That(Keys(), Is.EqualTo("fk_onl_fkc"));

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var refusal = Assert.Catch(() => Deploy(cmd, Package("BIGINT"), "REQUIRE"));
                Assert.Multiple(() =>
                {
                    Assert.That(refusal!.Message, Does.Contain("OnlineApply is Require"));
                    Assert.That(Keys(), Is.EqualTo("fk_onl_fkc"), "the refusal comes before the key is dropped, on every attempt");
                    Assert.That(Scalar(cmd, $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'onl_fkc' AND COLUMN_NAME = 'pid'")?.ToString(), Is.EqualTo("int"));
                });
            }

            Deploy(cmd, Package("BIGINT"), "PREFER");
            Assert.That(Keys(), Is.EqualTo("fk_onl_fkc"), "Prefer changes the type and puts the key back");
        }
        finally { Sql(cmd, $"DROP TABLE IF EXISTS `{_mainDb}`.`onl_fkc`; DROP TABLE IF EXISTS `{_mainDb}`.`onl_fkp`"); }
    }

    [Test]
    public void WhatIf_ShowsThePositionAndTheForms_AndChangesNothing()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Seed(cmd, "onl_wi");
            Deploy(cmd, Table("onl_wi", string.Join(", ", Col("id", nullable: false), Col("a"), Col("n"), Col("b"))), "PREFER", whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(Messages(cmd), Does.Contain("AFTER `a` (online: ALGORITHM=INSTANT, else INPLACE, LOCK=NONE)"));
                Assert.That(ColumnExists(cmd, "onl_wi", "n"), Is.False);
            });
        }
        finally { Drop(cmd, "onl_wi"); }
    }
}
