// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using MySqlConnector;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// SchemaSmith_RebuildTable on the MySQL family: the shadow-copy-and-swap engine. It is the only thing in
/// SchemaSmith that destroys user data, so every test here asserts what the USER ends up holding -- rows,
/// identifiers, column order -- rather than which statements were emitted to get there.
///
/// The procedure reads the declared definition from the _SchemaSmith_Tables / _SchemaSmith_Columns /
/// _SchemaSmith_Indexes temporary tables that SchemaSmith_ParseTableJson populates, so each test runs the
/// real parse procedure on its own connection and then calls the rebuild on that same connection -- MySQL
/// temporary tables are session-scoped, so the working set is still there. That is the procedure's actual
/// contract; faking the temp tables would test a different procedure.
///
/// MySQL has no object id to compare across a rebuild (no OBJECT_ID, no OID), so where a test needs to
/// prove the table was ACTUALLY replaced it puts a live-only index on the table that the declaration does
/// not mention. RebuildTable never re-creates secondary indexes -- that is an invariant of all three
/// engines -- so the marker surviving means no rebuild happened and the row assertions were vacuous.
///
/// Nothing here elects a rebuild -- these call the procedure directly, which keeps the engine's own
/// behaviour (refusals, auto-increment, row preservation) separable from the policy that points it at a
/// table. The decision that elects one lives in ModifiedTableQuench and is covered by
/// RebuildDecisionSharedTests.
/// </summary>
[Category("Integration")]
public abstract class RebuildTableSharedTests : BaseTableQuenchTests
{
    private const string MarkerIndex = "ix_rebuild_marker";

    // ---- harness ------------------------------------------------------------

    private IDbConnection OpenMainDb()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var use = conn.CreateCommand();
        use.CommandText = $"USE `{_mainDb}`";
        use.ExecuteNonQuery();
        return conn;
    }

    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Runs the genuine SchemaSmith_ParseTableJson, then calls the procedure under test on the same
    /// connection. Running the real parse rather than hand-building the temp tables is the point: the
    /// procedure consumes whatever the parse produces, and a hand-built working set would quietly diverge.
    /// </summary>
    protected void Rebuild(IDbCommand cmd, string json, string table, bool whatIf = false)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CALL SchemaSmith_ParseTableJson({Lit(_mainDb)}, {Lit(json)})";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL SchemaSmith_RebuildTable({Lit(_mainDb)}, {Lit(table)}, {(whatIf ? 1 : 0)})";
        cmd.ExecuteNonQuery();
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static long Long(IDbCommand cmd, string sql)
    {
        var v = Scalar(cmd, sql);
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt64(v);
    }

    private static int Int(IDbCommand cmd, string sql) => (int)Long(cmd, sql);

    private static List<string> Strings(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.IsDBNull(0) ? "<null>" : reader.GetValue(0).ToString() ?? "<null>");
        return results;
    }

    private int TableCount(IDbCommand cmd, string table)
        => Int(cmd, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                    + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)}");

    private int ColumnCount(IDbCommand cmd, string table, string column)
        => Int(cmd, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                    + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)} AND COLUMN_NAME = {Lit(column)}");

    private int MarkerIndexCount(IDbCommand cmd, string table)
        => Int(cmd, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                    + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)} AND INDEX_NAME = {Lit(MarkerIndex)}");

    private List<string> DeployedOrder(IDbCommand cmd, string table)
        => Strings(cmd, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS "
                        + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)} ORDER BY ORDINAL_POSITION");

    private static int AuditCount(IDbCommand cmd, string objectName, string actionType)
        => Int(cmd, "SELECT COUNT(*) FROM SchemaSmith_ChangeAudit "
                    + $"WHERE ObjectName = {Lit(objectName)} AND ActionType = {Lit(actionType)}");

    /// <summary>
    /// MySQL caps SIGNAL's MESSAGE_TEXT at 128 characters, so the procedure logs the full explanation to
    /// SchemaSmith_StatusMessages and signals a short line. Refusal tests read both -- the short message
    /// has to name the table, the log has to name the reason.
    /// </summary>
    private static string StatusLog(IDbCommand cmd)
    {
        // GROUP_CONCAT truncates at 1024 bytes by default and the refusal line is the LAST one written, so
        // the parse's own progress messages would push it off the end of the string this reads. Raised
        // explicitly rather than relying on the procedure under test having raised it for its own use.
        Exec(cmd, "SET SESSION group_concat_max_len = 1000000");
        return Scalar(cmd, "SELECT GROUP_CONCAT(Message SEPARATOR ' | ') FROM SchemaSmith_StatusMessages "
                           + "WHERE SessionId = CONNECTION_ID()")?.ToString() ?? "";
    }

    private static void ClearStatusLog(IDbCommand cmd)
        => Exec(cmd, "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()");

    // ---- rows survive -------------------------------------------------------

    [Test]
    public void Rebuild_PreservesEveryRow_AndAuditsTheRebuild()
    {
        var table = $"RbRows_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, KEY `{MarkerIndex}` (`Val`));
INSERT INTO `{table}` (`Id`, `Val`) VALUES (1, 'one'), (2, 'two'), (3, NULL);");

            // The marker stands in for the object id the other two engines compare: it is on the LIVE
            // table, it is not in the declaration, and RebuildTable never re-creates secondary indexes.
            // Without it every row assertion below would pass just as happily on a procedure that returned
            // without doing anything at all.
            Assert.That(MarkerIndexCount(cmd, table), Is.EqualTo(1),
                "Setup precondition: the marker index must exist before the rebuild, or the replacement "
                + "assertion below proves nothing.");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true}
                    ]
                }]
                """;
            Rebuild(cmd, json, table);

            Assert.That(MarkerIndexCount(cmd, table), Is.EqualTo(0),
                "The table must actually have been replaced. A surviving marker index means the original "
                + "table object is still there and the row assertions below are vacuous.");

            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}`"), Is.EqualTo(3),
                "Carrying the rows across IS the feature. A rebuild that loses rows is data destruction "
                + "with a successful exit code.");
            Assert.That(Strings(cmd, $"SELECT COALESCE(`Val`, '<null>') FROM `{table}` ORDER BY `Id`"),
                Is.EqualTo(new List<string> { "one", "two", "<null>" }),
                "Values must arrive intact and paired with the same keys -- a copy that shifts columns "
                + "would still produce three rows.");

            Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
                "A rebuild replaces a table wholesale; the run manifest has to say so, or an operator "
                + "reading the audit sees a table that changed with nothing accounting for it.");

            Assert.That(TableCount(cmd, $"{table}_SchemaSmithRebuild"), Is.EqualTo(0),
                "The shadow table must not survive the swap.");
            Assert.That(TableCount(cmd, $"{table}_SchemaSmithOld"), Is.EqualTo(0),
                "The renamed-out original must not survive the swap.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- the proven AUTO_INCREMENT defect ------------------------------------

    [Test]
    public void Rebuild_DoesNotReissueTheAutoIncrementOfADeletedHighestRow()
    {
        // THE regression test for the reseed defect. Ids 1-3 with id 3 deleted: the table's AUTO_INCREMENT
        // counter reads 4 (the NEXT value it will hand out) while max(id) is 2. Reseeding from the copied
        // data makes the next insert re-issue 3 -- an identifier the old table had already given to a row
        // that existed -- and nothing errors. Anything that recorded the old 3 (an audit row, an export, a
        // downstream system) then aliases two different entities. InnoDB will not even complain: it clamps
        // ALTER TABLE ... AUTO_INCREMENT upward to max+1, so the naive value is accepted as exactly the bug.
        var table = $"RbAutoInc_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL AUTO_INCREMENT, `Val` VARCHAR(50) NULL, PRIMARY KEY (`Id`));
INSERT INTO `{table}` (`Val`) VALUES ('a'), ('b'), ('c');
DELETE FROM `{table}` WHERE `Id` = 3;");

            // Guard the setup itself: if the seeding did not actually issue 3, the whole test is vacuous --
            // the next insert would get 4 for entirely innocent reasons and the assertion below would pass
            // while proving nothing.
            Assert.That(Long(cmd, "SELECT AUTO_INCREMENT FROM INFORMATION_SCHEMA.TABLES "
                                  + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)}"),
                Is.EqualTo(4L),
                "Setup precondition: the original counter must have issued 3 and stand at 4, and the row "
                + "must be gone.");
            Assert.That(Int(cmd, $"SELECT MAX(`Id`) FROM `{table}`"), Is.EqualTo(2),
                "Setup precondition: the surviving rows must stop at 2, so max(id) and the counter disagree.");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false, "AutoIncrement": true},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true}
                    ],
                    "Indexes": [
                        {"Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id"}
                    ]
                }]
                """;
            Rebuild(cmd, json, table);

            Assert.That(Strings(cmd, $"SELECT CONCAT(`Id`, '=', `Val`) FROM `{table}` ORDER BY `Id`"),
                Is.EqualTo(new List<string> { "1=a", "2=b" }),
                "The surviving rows must keep the identifiers they already had -- renumbering them would "
                + "break every reference held outside the database.");

            Exec(cmd, $"INSERT INTO `{table}` (`Val`) VALUES ('d')");

            Assert.That(Int(cmd, $"SELECT `Id` FROM `{table}` WHERE `Val` = 'd'"), Is.EqualTo(4),
                "The next insert after a rebuild must continue from the counter the ORIGINAL table had "
                + "reached, not from the largest surviving row. Getting 3 here is the silent defect: the "
                + "value was already issued once.");
            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}` WHERE `Id` = 3"), Is.EqualTo(0),
                "Id 3 was handed out before the rebuild and must never be handed out again.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- new column ---------------------------------------------------------

    [Test]
    public void Rebuild_NewDeclaredColumn_ArrivesDefaultedInsteadOfBreakingTheCopy()
    {
        var table = $"RbNewCol_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL);
INSERT INTO `{table}` (`Id`, `Val`) VALUES (1, 'one'), (2, 'two');");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true},
                        {"Name": "Added", "DataType": "INT", "Nullable": false, "Default": "42"},
                        {"Name": "AlsoAdded", "DataType": "VARCHAR(20)", "Nullable": true}
                    ]
                }]
                """;
            Rebuild(cmd, json, table);

            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}`"), Is.EqualTo(2),
                "A column the live table does not have must not appear in the SELECT list -- if it did, "
                + "the copy would fail outright and take the rows with it.");
            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}` WHERE `Added` = 42"), Is.EqualTo(2),
                "A new NOT NULL column with a DEFAULT must arrive at that default on every carried-over row.");
            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}` WHERE `AlsoAdded` IS NULL"), Is.EqualTo(2),
                "A new nullable column with no default must arrive NULL rather than blocking the copy.");
            Assert.That(Strings(cmd, $"SELECT `Val` FROM `{table}` ORDER BY `Id`"),
                Is.EqualTo(new List<string> { "one", "two" }),
                "The pre-existing data must be unchanged by the arrival of new columns.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- removed column -----------------------------------------------------

    [Test]
    public void Rebuild_ColumnRemovedFromTheDefinition_IsGoneAfterwards()
    {
        var table = $"RbDropCol_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Keep` VARCHAR(50) NULL, `Obsolete` VARCHAR(50) NULL);
INSERT INTO `{table}` (`Id`, `Keep`, `Obsolete`) VALUES (1, 'kept', 'junk'), (2, 'also kept', 'junk');");

            Assert.That(ColumnCount(cmd, table, "Obsolete"), Is.EqualTo(1),
                "Setup precondition: the column being removed must exist beforehand.");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Keep", "DataType": "VARCHAR(50)", "Nullable": true}
                    ]
                }]
                """;
            Rebuild(cmd, json, table);

            Assert.That(ColumnCount(cmd, table, "Obsolete"), Is.EqualTo(0),
                "A column the package no longer declares must not survive the rebuild -- it appears in "
                + "neither the shadow's CREATE nor the copy.");
            Assert.That(Strings(cmd, $"SELECT `Keep` FROM `{table}` ORDER BY `Id`"),
                Is.EqualTo(new List<string> { "kept", "also kept" }),
                "Dropping one column must not disturb the columns either side of it.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- declared column order ---------------------------------------------

    [Test]
    public void Rebuild_HonoursDeclaredColumnOrder()
    {
        var table = $"RbOrder_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Alpha` INT NULL, `Bravo` INT NULL, `Charlie` INT NULL);
INSERT INTO `{table}` (`Alpha`, `Bravo`, `Charlie`) VALUES (1, 2, 3);");

            Assert.That(DeployedOrder(cmd, table), Is.EqualTo(new List<string> { "Alpha", "Bravo", "Charlie" }),
                "Setup precondition: the deployed order must START different from the declared order, or "
                + "the order assertion below would hold no matter what the procedure did.");

            // Declared in a deliberately different order from the deployed table.
            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Charlie", "DataType": "INT", "Nullable": true},
                        {"Name": "Alpha", "DataType": "INT", "Nullable": true},
                        {"Name": "Bravo", "DataType": "INT", "Nullable": true}
                    ]
                }]
                """;
            Rebuild(cmd, json, table);

            Assert.That(DeployedOrder(cmd, table), Is.EqualTo(new List<string> { "Charlie", "Alpha", "Bravo" }),
                "The rebuilt table must be laid out in the order the package file declares -- putting the "
                + "deployed order back is the one thing an in-place ALTER can never do.");
            Assert.That(Strings(cmd, $"SELECT CONCAT(`Alpha`, '/', `Bravo`, '/', `Charlie`) FROM `{table}`"),
                Is.EqualTo(new List<string> { "1/2/3" }),
                "Reordering the columns must move the DATA with them. A positional copy would land 3/1/2 "
                + "here and still report the right column order.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- inbound foreign key ------------------------------------------------

    [Test]
    public void Rebuild_InboundForeignKey_DoesNotBlockTheRebuild_AndTheChildSurvives()
    {
        // MySQL rewrites a referencing foreign key to follow a RENAME TABLE, so after the swap the child is
        // constrained against the table that was moved aside. DROP TABLE then refuses while that key still
        // points at it -- which is what makes the problem visible rather than what causes it. This engine
        // drops the inbound keys AFTER the atomic swap (they are reversible work no longer, but the swap has
        // already happened) and leaves the re-add to the owning table's foreign-key pass.
        var uid = Uid();
        var parent = $"RbFkParent_{uid}";
        var child = $"RbFkChild_{uid}";
        var fkName = $"fk_rbfkchild_{uid}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{parent}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, PRIMARY KEY (`Id`));
INSERT INTO `{parent}` (`Id`, `Val`) VALUES (1, 'p1'), (2, 'p2');
CREATE TABLE `{child}` (`Id` INT NOT NULL, `ParentId` INT NOT NULL, PRIMARY KEY (`Id`),
  CONSTRAINT `{fkName}` FOREIGN KEY (`ParentId`) REFERENCES `{parent}` (`Id`));
INSERT INTO `{child}` (`Id`, `ParentId`) VALUES (10, 1), (11, 2);");

            var inboundKeyCount = "SELECT COUNT(DISTINCT CONSTRAINT_NAME) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE "
                                  + $"WHERE REFERENCED_TABLE_SCHEMA = {Lit(_mainDb)} AND REFERENCED_TABLE_NAME = {Lit(parent)}";
            var childKeyCount = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                                + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(child)} AND CONSTRAINT_TYPE = 'FOREIGN KEY'";

            Assert.That(Int(cmd, inboundKeyCount), Is.EqualTo(1),
                "Setup precondition: an inbound foreign key must exist, or this test proves nothing.");

            var json = $$"""
                [{
                    "Name": "{{parent}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true}
                    ],
                    "Indexes": [
                        {"Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id"}
                    ]
                }]
                """;
            Rebuild(cmd, json, parent);

            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{parent}`"), Is.EqualTo(2),
                "The parent's rows must survive a rebuild the inbound key would otherwise have blocked.");
            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{child}`"), Is.EqualTo(2),
                "The child table and its rows are not this rebuild's business and must be untouched.");

            Assert.That(TableCount(cmd, $"{parent}_SchemaSmithOld"), Is.EqualTo(0),
                "The renamed-out original must be gone. If the inbound key had not been dropped, the DROP "
                + "would have failed and this table would still be sitting there -- with the child "
                + "constrained against it instead of against the replacement.");

            Assert.That(Int(cmd, childKeyCount), Is.EqualTo(0),
                "The inbound key is deliberately NOT re-added here: it is declared in the CHILD's JSON, so "
                + "the child's foreign-key quench pass re-creates it. What must never happen is the key "
                + "surviving pointed at the renamed-away table.");

            Assert.That(AuditCount(cmd, $"{child}.{fkName}", "dropped"), Is.EqualTo(1),
                "A key this rebuild removed and does not put back has to appear in the run manifest, or "
                + "the operator has no record that referential integrity was briefly dropped on their behalf.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{child}`");
            Exec(cmd, $"DROP TABLE IF EXISTS `{parent}`, `{parent}_SchemaSmithRebuild`, `{parent}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- refusal ------------------------------------------------------------

    [Test]
    public void Rebuild_WorkingNameAlreadyInUse_IsRefused_AndTheErrorNamesTheTable()
    {
        // The leftover-shadow refusal, which on this engine is load-bearing in a way it is not on the other
        // two: MySQL DDL is not transactional, so a rebuild that dies between the shadow CREATE and the swap
        // strands that shadow. There is no rollback to clean it up, and this refusal is what stops the next
        // run overwriting whatever it holds -- it forces a human to look at it instead.
        var table = $"RbCollide_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, KEY `{MarkerIndex}` (`Val`));
INSERT INTO `{table}` (`Id`, `Val`) VALUES (1, 'one'), (2, 'two');
CREATE TABLE `{table}_SchemaSmithRebuild` (`Id` INT NOT NULL);");

            Assert.That(TableCount(cmd, $"{table}_SchemaSmithRebuild"), Is.EqualTo(1),
                "Setup precondition: the leftover shadow must exist, or the refusal below is not being tested.");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true}
                    ]
                }]
                """;

            ClearStatusLog(cmd);
            var ex = Assert.Throws<MySqlException>(() => Rebuild(cmd, json, table));

            // Asserted non-null first so a failure names the missing message rather than reporting a
            // string mismatch against null.
            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.Message, Does.Contain(table),
                $"The refusal must name the table, since a deploy touches many. Got: '{ex.Message}'.");

            var log = StatusLog(cmd);
            Assert.That(log, Does.Contain("already in use"),
                "MySQL truncates a signalled message at 128 characters, so the reason lives in the run log. "
                + $"It has to say what is in the way. Log: {log}");

            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}`"), Is.EqualTo(2),
                "The refusal must fire BEFORE any DDL -- the table and its rows must be exactly as they were.");
            Assert.That(MarkerIndexCount(cmd, table), Is.EqualTo(1),
                "And the table object itself must be the original one, not a replacement built anyway.");
            Assert.That(ColumnCount(cmd, $"{table}_SchemaSmithRebuild", "Val"), Is.EqualTo(0),
                "The leftover must be left exactly as found. The declaration has a Val column and the "
                + "leftover does not, so a Val appearing here would mean the run rebuilt over the top of "
                + "it -- which is the one thing this refusal exists to prevent.");

            // A preview that hid the refusal would tell the operator a rebuild is available where it is not.
            var whatIfEx = Assert.Throws<MySqlException>(() => Rebuild(cmd, json, table, whatIf: true));
            Assert.That(whatIfEx, Is.Not.Null);
            Assert.That(whatIfEx!.Message, Does.Contain(table),
                $"WhatIf must surface the impossibility rather than printing a rebuild that could never "
                + $"run. Got: '{whatIfEx.Message}'.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            ClearStatusLog(cmd);
            conn.Close();
        }
    }

    // ---- WhatIf -------------------------------------------------------------

    [Test]
    public void Rebuild_WhatIf_ChangesNothing_AndEmitsWouldRebuild()
    {
        var table = $"RbWhatIf_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        try
        {
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, KEY `{MarkerIndex}` (`Val`));
INSERT INTO `{table}` (`Id`, `Val`) VALUES (1, 'one'), (2, 'two');");

            // Declares a change (a reordered, extended column set) so the preview has something real to
            // describe -- a WhatIf over a table that already matches would leave nothing to change anyway.
            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true},
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Added", "DataType": "INT", "Nullable": true}
                    ]
                }]
                """;
            Rebuild(cmd, json, table, whatIf: true);

            Assert.That(MarkerIndexCount(cmd, table), Is.EqualTo(1),
                "WhatIf must not replace the table. The marker index only survives on the original object.");
            Assert.That(DeployedOrder(cmd, table), Is.EqualTo(new List<string> { "Id", "Val" }),
                "WhatIf must not add the declared column or reorder the deployed ones -- the shape must be "
                + "exactly what it was. This is what makes the preview safe to run against production.");
            Assert.That(Int(cmd, $"SELECT COUNT(*) FROM `{table}`"), Is.EqualTo(2),
                "WhatIf must not move any rows.");
            Assert.That(TableCount(cmd, $"{table}_SchemaSmithRebuild"), Is.EqualTo(0),
                "WhatIf must not create the shadow table.");

            Assert.That(AuditCount(cmd, table, "wouldRebuild"), Is.EqualTo(1),
                "The preview has to be visible in the run manifest, or a WhatIf report cannot show that a "
                + "rebuild was going to happen at all.");
            Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(0),
                "A preview must never be recorded as work that happened.");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`");
            conn.Close();
        }
    }

    // ---- grants survive --------------------------------------------------------

    private List<string> GrantsFor(IDbCommand cmd, string table, string user) => Strings(cmd, $@"
SELECT PRIVILEGE_TYPE FROM INFORMATION_SCHEMA.TABLE_PRIVILEGES
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND GRANTEE = '''{user}''@''%'''
UNION ALL
SELECT CONCAT(PRIVILEGE_TYPE, ' (', COLUMN_NAME, ')') FROM INFORMATION_SCHEMA.COLUMN_PRIVILEGES
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND GRANTEE = '''{user}''@''%'''
ORDER BY 1");

    // MySQL and MariaDB key a table or column grant by name, not by the table object, so the rename swap leaves it
    // on the replacement with no help. This pins that: a rebuild that dropped and re-created under the same name
    // would also pass, which is the point -- what the user keeps is the grant.
    [Test]
    public void Rebuild_KeepsTableAndColumnGrants()
    {
        var table = $"RbGrants_{Uid()}";
        var user = $"rb_perm_{Uid()}";
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        try
        {
            Exec(cmd, $"CREATE USER '{user}'@'%' IDENTIFIED BY 'Rb-perm-pw1!'");
            Exec(cmd, $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, PRIMARY KEY (`Id`));
INSERT INTO `{table}` (`Id`, `Val`) VALUES (1, 'one');");
            Exec(cmd, $"GRANT SELECT ON `{_mainDb}`.`{table}` TO '{user}'@'%'");
            Exec(cmd, $"GRANT UPDATE (`Val`) ON `{_mainDb}`.`{table}` TO '{user}'@'%'");
            var before = GrantsFor(cmd, table, user);
            Assert.That(before, Is.EqualTo(new List<string> { "SELECT", "UPDATE (Val)" }),
                "setup: both grants must be on the table before the rebuild, or the comparison below is vacuous");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true}
                    ]
                }]
                """;
            Rebuild(cmd, json, table);

            Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
                "The table must actually have been rebuilt, or the grant assertion below proves nothing.");
            Assert.That(GrantsFor(cmd, table, user), Is.EqualTo(before),
                "The table and column grants must still be on the replacement after the swap.");
        }
        finally
        {
            try
            {
                Exec(cmd, $"DROP USER IF EXISTS '{user}'@'%'");
                Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
            }
            catch (MySqlException) { /* best-effort cleanup must not mask a test result */ }
        }
    }
}
