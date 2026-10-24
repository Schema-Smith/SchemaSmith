// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// SchemaSmith.RebuildTable on PostgreSQL: the shadow-copy-and-swap engine. It is the only thing in
/// SchemaSmith that destroys user data, so every test here asserts what the USER ends up holding -- rows,
/// identifiers, sequence names, column order -- rather than which statements were emitted to get there.
///
/// The procedure reads the declared definition from the temp_tables / temp_columns temporary tables that
/// ParseTableJsonIntoTempTables populates, so each test runs the real parse script (the same text
/// ForgeKindler bakes into TableQuench) in a DO block on its own connection and then calls the procedure on
/// that same connection -- PostgreSQL temporary tables are session-scoped, so the working set is still there.
/// That is the procedure's actual contract; faking the temp tables would test a different procedure.
///
/// Nothing here elects a rebuild -- these call the procedure directly, which keeps the engine's own
/// behaviour (refusals, sequence position, row preservation) separable from the policy that points it at a
/// table. The decision that elects one lives in ModifiedTableQuench and is covered by RebuildDecisionTests.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class RebuildTableTests : BaseTableQuenchTests
{
    private static readonly string ParseJsonScript = ForgeKindler.GetParseTableJsonScript(Platform.PostgreSQL);

    // ---- harness ------------------------------------------------------------

    private IDbConnection OpenMainDb()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// The real ParseTableJsonIntoTempTables body, wrapped in the same variable frame TableQuench gives it
    /// (table_json / sql_script / p_UpdateFillFactor). Running the genuine parse rather than hand-building
    /// the temp tables is the point: the procedure under test consumes whatever the parse produces, and a
    /// hand-built working set would quietly diverge from it.
    /// </summary>
    private static string ParseBatch(string json)
    {
        var arrayJson = json.TrimStart().StartsWith("[", StringComparison.Ordinal) ? json : "[" + json + "]";
        return "DO $SchemaSmithRebuildTest$\r\n"
               + "DECLARE\r\n"
               + "  table_json TEXT = " + Lit(arrayJson) + ";\r\n"
               + "  sql_script TEXT = '';\r\n"
               + "  p_UpdateFillFactor BOOLEAN = FALSE;\r\n"
               + "BEGIN\r\n"
               + ParseJsonScript + "\r\n"
               + "END $SchemaSmithRebuildTest$;";
    }

    private static void Rebuild(IDbCommand cmd, string json, string schema, string table, bool whatIf = false)
    {
        cmd.CommandTimeout = 120;
        cmd.CommandText = ParseBatch(json);
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CALL \"SchemaSmith\".\"RebuildTable\"({Lit(schema)}, {Lit(table)}, {(whatIf ? "TRUE" : "FALSE")});";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 120;
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
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.IsDBNull(0) ? null : reader.GetValue(0).ToString());
        return results;
    }

    /// <summary>-1 when the relation does not exist, so "no such object" is an unambiguous value rather
    /// than a null/DBNull ambiguity in the assertion.</summary>
    private static long Oid(IDbCommand cmd, string schema, string name)
        => Long(cmd, $"SELECT COALESCE(to_regclass('\"{schema}\".\"{name}\"')::oid::BIGINT, -1)");

    private static int AuditCount(IDbCommand cmd, string objectName, string actionType)
        => Int(cmd, "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" "
                    + $"WHERE \"ObjectName\" = {Lit(objectName)} AND \"ActionType\" = {Lit(actionType)}");

    private static string NewSchema(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    // ---- rows survive -------------------------------------------------------

    [Test]
    public void Rebuild_PreservesEveryRow_AndAuditsTheRebuild()
    {
        var schema = NewSchema("RbRows");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""RowsSurvive"" (""Id"" INT NOT NULL, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""RowsSurvive"" (""Id"", ""Val"") VALUES (1, 'one'), (2, 'two'), (3, NULL);");

        // Captured so the assertions below cannot pass on a procedure that did nothing at all: if no rebuild
        // happened the rows would obviously still be there, and every row assertion would be vacuous. A new
        // OID is the only proof the table was actually replaced.
        var oidBefore = Oid(cmd, schema, "RowsSurvive");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "RowsSurvive",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false},
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "RowsSurvive");

        Assert.That(Oid(cmd, schema, "RowsSurvive"), Is.Not.EqualTo(oidBefore),
            "The table must actually have been replaced. Without this the row assertions below would pass "
            + "just as happily on a procedure that returned without doing anything.");

        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""RowsSurvive"""), Is.EqualTo(3),
            "Carrying the rows across IS the feature. A rebuild that loses rows is data destruction with a "
            + "successful exit code.");
        Assert.That(Strings(cmd, $@"SELECT COALESCE(""Val"", '<null>') FROM ""{schema}"".""RowsSurvive"" ORDER BY ""Id"""),
            Is.EqualTo(new List<string> { "one", "two", "<null>" }),
            "Values must arrive intact and paired with the same keys -- a copy that shifts columns would "
            + "still produce three rows.");

        Assert.That(AuditCount(cmd, $"{schema}.RowsSurvive", "rebuilt"), Is.EqualTo(1),
            "A rebuild replaces a table wholesale; the run manifest has to say so, or an operator reading "
            + "the audit sees a table that changed with nothing accounting for it.");

        Assert.That(Oid(cmd, schema, "RowsSurvive_SchemaSmithRebuild"), Is.EqualTo(-1),
            "The shadow table must not survive the swap.");
        Assert.That(Oid(cmd, schema, "RowsSurvive_SchemaSmithOld"), Is.EqualTo(-1),
            "The renamed-out original must not survive the swap.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- the proven sequence-position defect --------------------------------

    [Test]
    public void Rebuild_DoesNotReissueTheIdentityOfADeletedHighestRow()
    {
        // THE regression test for the reseed defect. Ids 1-3 with id 3 deleted: the sequence's last_value is
        // 3 (the last value HANDED OUT) while max(id) is 2 (the largest still present). Restoring to the
        // copied max makes the next insert re-issue 3 -- an identifier the old table had already given to a
        // row that existed. PostgreSQL is harsher than SQL Server about the naive path (an explicit-value
        // copy does not advance the shadow's sequence at all, so leaving it alone fails on id 1 immediately)
        // but both wrong answers end in a collision or an alias, so the position is captured and restored.
        var schema = NewSchema("RbSeqPos");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""IdentityReuse"" (""Id"" INT GENERATED ALWAYS AS IDENTITY, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""IdentityReuse"" (""Val"") VALUES ('a'), ('b'), ('c');
DELETE FROM ""{schema}"".""IdentityReuse"" WHERE ""Id"" = 3;");

        // Guard the setup itself: if the seeding did not actually issue 3, the whole test is vacuous -- the
        // next insert would get 3 for entirely innocent reasons and the assertion below would pass while
        // proving nothing.
        Assert.That(Long(cmd, $@"SELECT last_value FROM pg_sequences WHERE schemaname = '{schema}' AND sequencename = 'IdentityReuse_Id_seq'"),
            Is.EqualTo(3L),
            "Setup precondition: the original sequence must have issued 3 and the row must be gone.");
        Assert.That(Int(cmd, $@"SELECT MAX(""Id"") FROM ""{schema}"".""IdentityReuse"""), Is.EqualTo(2),
            "Setup precondition: the surviving rows must stop at 2, so max(id) and the sequence disagree.");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "IdentityReuse",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false, "Generated": "GENERATED ALWAYS AS IDENTITY"},
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "IdentityReuse");

        Assert.That(Strings(cmd, $@"SELECT ""Id"" || '=' || ""Val"" FROM ""{schema}"".""IdentityReuse"" ORDER BY ""Id"""),
            Is.EqualTo(new List<string> { "1=a", "2=b" }),
            "The surviving rows must keep the identifiers they already had -- renumbering them would break "
            + "every reference held outside the database. (This also proves the copy used OVERRIDING SYSTEM "
            + "VALUE: a GENERATED ALWAYS identity column rejects an explicit value without it.)");

        Exec(cmd, $@"INSERT INTO ""{schema}"".""IdentityReuse"" (""Val"") VALUES ('d')");

        Assert.That(Int(cmd, $@"SELECT ""Id"" FROM ""{schema}"".""IdentityReuse"" WHERE ""Val"" = 'd'"), Is.EqualTo(4),
            "The next insert after a rebuild must continue from the position the ORIGINAL sequence had "
            + "reached (3), not from the largest surviving row (2). Getting 3 here is the silent defect: the "
            + "value was already issued once.");
        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""IdentityReuse"" WHERE ""Id"" = 3"), Is.EqualTo(0),
            "Id 3 was handed out before the rebuild and must never be handed out again.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- the PostgreSQL-only sequence-NAME defect ---------------------------

    [Test]
    public void Rebuild_TwiceInARow_LeavesExactlyOneSequenceUnderItsNaturalName()
    {
        // THE PostgreSQL-only trap, and it needs TWO passes to be visible. A sequence does not follow a table
        // rename, so a rebuild through <table>_SchemaSmithRebuild leaves the surviving table using
        // <table>_SchemaSmithRebuild_Id_seq. The data is correct either way, so the single-pass tests above
        // all pass over this; what a second pass shows is that the wrong name is not a one-off cosmetic
        // blemish but something a table accumulates every time it is rebuilt, surfacing later in a pg_dump or
        // a puzzled \d. Asserting the COUNT of sequences in the schema (not merely that the natural name
        // exists) is what makes a stray left-behind sequence fail rather than hide.
        var schema = NewSchema("RbSeqName");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""SeqName"" (""Id"" INT GENERATED ALWAYS AS IDENTITY, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""SeqName"" (""Val"") VALUES ('a'), ('b');");

        var sequenceNames = $@"
SELECT s.relname
  FROM pg_class s
  JOIN pg_namespace n ON n.oid = s.relnamespace
  WHERE n.nspname = '{schema}' AND s.relkind = 'S'
  ORDER BY s.relname";

        Assert.That(Strings(cmd, sequenceNames), Is.EqualTo(new List<string> { "SeqName_Id_seq" }),
            "Setup precondition: the identity column must start out owning exactly one sequence under the "
            + "name PostgreSQL derives from the table -- that is the name the rebuild has to give back.");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "SeqName",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false, "Generated": "GENERATED ALWAYS AS IDENTITY"},
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;

        var oidStart = Oid(cmd, schema, "SeqName");
        Rebuild(cmd, json, schema, "SeqName");
        var oidAfterFirst = Oid(cmd, schema, "SeqName");

        Assert.That(oidAfterFirst, Is.Not.EqualTo(oidStart), "The first pass must actually replace the table.");
        Assert.That(Strings(cmd, sequenceNames), Is.EqualTo(new List<string> { "SeqName_Id_seq" }),
            "After one rebuild the surviving table must own exactly one sequence, still under its natural "
            + "name. A SeqName_SchemaSmithRebuild_Id_seq here is the defect -- correct data, wrong catalog.");

        Rebuild(cmd, json, schema, "SeqName");

        Assert.That(Oid(cmd, schema, "SeqName"), Is.Not.EqualTo(oidAfterFirst),
            "The second pass must actually replace the table too, or this test proves nothing about "
            + "repeated rebuilds.");
        Assert.That(Strings(cmd, sequenceNames), Is.EqualTo(new List<string> { "SeqName_Id_seq" }),
            "Two rebuilds must still leave exactly ONE sequence under the natural name. This is where an "
            + "unfixed rename compounds: a second _SchemaSmithRebuild suffix, or a second orphaned sequence.");

        // The rows and the sequence position have to survive both passes -- a rename fix that reset the
        // sequence would satisfy the name assertions above while quietly re-issuing identifiers.
        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""SeqName"""), Is.EqualTo(2),
            "Both rows must survive both rebuilds.");
        Exec(cmd, $@"INSERT INTO ""{schema}"".""SeqName"" (""Val"") VALUES ('c')");
        Assert.That(Int(cmd, $@"SELECT ""Id"" FROM ""{schema}"".""SeqName"" WHERE ""Val"" = 'c'"), Is.EqualTo(3),
            "The sequence must still be positioned past the copied rows after the second rebuild.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- new column ---------------------------------------------------------

    [Test]
    public void Rebuild_NewDeclaredColumn_ArrivesDefaultedInsteadOfBreakingTheCopy()
    {
        var schema = NewSchema("RbNewCol");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""NewColumn"" (""Id"" INT NOT NULL, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""NewColumn"" (""Id"", ""Val"") VALUES (1, 'one'), (2, 'two');");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "NewColumn",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false},
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true},
                    {"Name": "Added", "DataType": "INT", "Nullable": false, "Default": "42"},
                    {"Name": "AlsoAdded", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "NewColumn");

        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""NewColumn"""), Is.EqualTo(2),
            "A column the live table does not have must not appear in the SELECT list -- if it did, the copy "
            + "would fail outright and take the rows with it.");
        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""NewColumn"" WHERE ""Added"" = 42"), Is.EqualTo(2),
            "A new NOT NULL column with a DEFAULT must arrive at that default on every carried-over row.");
        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""NewColumn"" WHERE ""AlsoAdded"" IS NULL"), Is.EqualTo(2),
            "A new nullable column with no default must arrive NULL rather than blocking the copy.");
        Assert.That(Strings(cmd, $@"SELECT ""Val"" FROM ""{schema}"".""NewColumn"" ORDER BY ""Id"""),
            Is.EqualTo(new List<string> { "one", "two" }),
            "The pre-existing data must be unchanged by the arrival of new columns.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- removed column -----------------------------------------------------

    [Test]
    public void Rebuild_ColumnRemovedFromTheDefinition_IsGoneAfterwards()
    {
        var schema = NewSchema("RbDropCol");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""DropColumn"" (""Id"" INT NOT NULL, ""Keep"" TEXT NULL, ""Obsolete"" TEXT NULL);
INSERT INTO ""{schema}"".""DropColumn"" (""Id"", ""Keep"", ""Obsolete"") VALUES (1, 'kept', 'junk'), (2, 'also kept', 'junk');");

        var obsoleteColumnCount = "SELECT COUNT(*) FROM information_schema.columns "
                                  + $"WHERE table_schema = '{schema}' AND table_name = 'DropColumn' AND column_name = 'Obsolete'";

        Assert.That(Int(cmd, obsoleteColumnCount), Is.EqualTo(1),
            "Setup precondition: the column being removed must exist beforehand.");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "DropColumn",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false},
                    {"Name": "Keep", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "DropColumn");

        Assert.That(Int(cmd, obsoleteColumnCount), Is.EqualTo(0),
            "A column the package no longer declares must not survive the rebuild -- it appears in neither "
            + "the shadow's CREATE nor the copy.");
        Assert.That(Strings(cmd, $@"SELECT ""Keep"" FROM ""{schema}"".""DropColumn"" ORDER BY ""Id"""),
            Is.EqualTo(new List<string> { "kept", "also kept" }),
            "Dropping one column must not disturb the columns either side of it.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- declared column order ---------------------------------------------

    [Test]
    public void Rebuild_HonoursDeclaredColumnOrder()
    {
        var schema = NewSchema("RbOrder");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""ColumnOrder"" (""Alpha"" INT NULL, ""Bravo"" INT NULL, ""Charlie"" INT NULL);
INSERT INTO ""{schema}"".""ColumnOrder"" (""Alpha"", ""Bravo"", ""Charlie"") VALUES (1, 2, 3);");

        var deployedOrder = "SELECT column_name FROM information_schema.columns "
                            + $"WHERE table_schema = '{schema}' AND table_name = 'ColumnOrder' ORDER BY ordinal_position";

        Assert.That(Strings(cmd, deployedOrder), Is.EqualTo(new List<string> { "Alpha", "Bravo", "Charlie" }),
            "Setup precondition: the deployed order must START different from the declared order, or the "
            + "order assertion below would hold no matter what the procedure did.");

        // Declared in a deliberately different order from the deployed table.
        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "ColumnOrder",
                "Columns": [
                    {"Name": "Charlie", "DataType": "INT", "Nullable": true},
                    {"Name": "Alpha", "DataType": "INT", "Nullable": true},
                    {"Name": "Bravo", "DataType": "INT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "ColumnOrder");

        Assert.That(Strings(cmd, deployedOrder), Is.EqualTo(new List<string> { "Charlie", "Alpha", "Bravo" }),
            "The rebuilt table must be laid out in the order the package file declares -- putting the "
            + "deployed order back is the one thing an in-place ALTER can never do.");

        Assert.That(Strings(cmd, $@"SELECT ""Alpha"" || '/' || ""Bravo"" || '/' || ""Charlie"" FROM ""{schema}"".""ColumnOrder"""),
            Is.EqualTo(new List<string> { "1/2/3" }),
            "Reordering the columns must move the DATA with them. A positional copy would land 3/1/2 here "
            + "and still report the right column order.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- inbound foreign key ------------------------------------------------

    [Test]
    public void Rebuild_InboundForeignKey_DoesNotBlockTheRebuild_AndTheChildSurvives()
    {
        // An inbound foreign key FOLLOWS a table rename on PostgreSQL, so after a naive swap the child would
        // be constrained against the table that was moved aside instead of the one that replaced it. The DROP
        // then failing is merely what surfaces that. The engine drops inbound keys before the swap and leaves
        // the re-add to the owning table's foreign-key pass.
        var schema = NewSchema("RbFk");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""FkParent"" (""Id"" INT NOT NULL CONSTRAINT ""pk_fkparent"" PRIMARY KEY, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""FkParent"" (""Id"", ""Val"") VALUES (1, 'p1'), (2, 'p2');
CREATE TABLE ""{schema}"".""FkChild"" (""Id"" INT NOT NULL, ""ParentId"" INT NOT NULL,
  CONSTRAINT ""fk_fkchild_parent"" FOREIGN KEY (""ParentId"") REFERENCES ""{schema}"".""FkParent"" (""Id""));
INSERT INTO ""{schema}"".""FkChild"" (""Id"", ""ParentId"") VALUES (10, 1), (11, 2);");

        var inboundKeyCount = "SELECT COUNT(*) FROM pg_constraint con "
                              + $"WHERE con.contype = 'f' AND con.confrelid = '\"{schema}\".\"FkParent\"'::regclass";
        var childKeyCount = "SELECT COUNT(*) FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid "
                            + "JOIN pg_namespace n ON n.oid = c.relnamespace "
                            + $"WHERE con.contype = 'f' AND n.nspname = '{schema}' AND c.relname = 'FkChild'";

        Assert.That(Int(cmd, inboundKeyCount), Is.EqualTo(1),
            "Setup precondition: an inbound foreign key must exist, or this test proves nothing.");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "FkParent",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false},
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "FkParent");

        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""FkParent"""), Is.EqualTo(2),
            "The parent's rows must survive a rebuild it was only blocked from by an inbound key.");
        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""FkChild"""), Is.EqualTo(2),
            "The child table and its rows are not this rebuild's business and must be untouched.");

        Assert.That(Oid(cmd, schema, "FkParent_SchemaSmithOld"), Is.EqualTo(-1),
            "The renamed-out original must be gone. If the inbound key had not been dropped first, the DROP "
            + "would have failed and this table would still be sitting there -- with the child pointing at it.");

        Assert.That(Int(cmd, childKeyCount), Is.EqualTo(0),
            "The inbound key is deliberately NOT re-added here: it is declared in the CHILD's JSON, so the "
            + "child's foreign-key quench pass re-creates it. What must never happen is the key surviving "
            + "pointed at the renamed-away table.");

        Assert.That(AuditCount(cmd, $"{schema}.FkChild.fk_fkchild_parent", "dropped"), Is.EqualTo(1),
            "A key this rebuild removed and does not put back has to appear in the run manifest, or the "
            + "operator has no record that referential integrity was briefly dropped on their behalf.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- refusal ------------------------------------------------------------

    [Test]
    public void Rebuild_TableWithInheritanceChildren_IsRefused_AndTheErrorNamesTheReasonAndTheTable()
    {
        var schema = NewSchema("RbBlocked");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""InheritBlocked"" (""Id"" INT NOT NULL, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""InheritBlocked"" (""Id"", ""Val"") VALUES (1, 'one'), (2, 'two');
CREATE TABLE ""{schema}"".""InheritChild"" () INHERITS (""{schema}"".""InheritBlocked"");");

        Assert.That(Int(cmd, $"SELECT COUNT(*) FROM pg_inherits WHERE inhparent = '\"{schema}\".\"InheritBlocked\"'::regclass"),
            Is.EqualTo(1),
            "Setup precondition: the inheritance edge must actually exist, or the refusal below is not being "
            + "tested.");

        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "InheritBlocked",
                "Columns": [
                    {"Name": "Id", "DataType": "INT", "Nullable": false},
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true}
                ]
            }
            """;

        var ex = Assert.Throws<PostgresException>(() => Rebuild(cmd, json, schema, "InheritBlocked"));

        // Asserted non-null first so a failure names the missing message rather than reporting a string
        // mismatch against null.
        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.Message, Does.Contain("inherit"),
            "The refusal must name the blocking state. 'This table cannot be rebuilt' leaves the operator no "
            + $"way to know what to detach or migrate. Got: '{ex.Message}'.");
        Assert.That(ex.Message, Does.Contain("InheritBlocked"),
            $"The refusal must name the table, since a deploy touches many. Got: '{ex.Message}'.");

        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ONLY ""{schema}"".""InheritBlocked"""), Is.EqualTo(2),
            "The refusal must fire BEFORE any DDL -- the table and its rows must be exactly as they were.");
        Assert.That(Oid(cmd, schema, "InheritBlocked_SchemaSmithRebuild"), Is.EqualTo(-1),
            "No shadow table may exist after a refusal; a half-built rebuild is worse than none.");

        // A preview that hid the refusal would tell the operator a rebuild is available on a table where it
        // can never be, so the guard has to fire in WhatIf too.
        var whatIfEx = Assert.Throws<PostgresException>(() => Rebuild(cmd, json, schema, "InheritBlocked", whatIf: true));
        Assert.That(whatIfEx, Is.Not.Null);
        Assert.That(whatIfEx!.Message, Does.Contain("inherit"),
            "WhatIf must surface the impossibility rather than printing a rebuild that could never run. "
            + $"Got: '{whatIfEx.Message}'.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- WhatIf -------------------------------------------------------------

    [Test]
    public void Rebuild_WhatIf_ChangesNothing_AndEmitsWouldRebuild()
    {
        var schema = NewSchema("RbWhatIf");
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, $@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""WhatIf"" (""Id"" INT NOT NULL, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""WhatIf"" (""Id"", ""Val"") VALUES (1, 'one'), (2, 'two');");

        var oidBefore = Oid(cmd, schema, "WhatIf");
        var deployedOrder = "SELECT column_name FROM information_schema.columns "
                            + $"WHERE table_schema = '{schema}' AND table_name = 'WhatIf' ORDER BY ordinal_position";

        // Declares a change (a reordered, extended column set) so the preview has something real to describe
        // -- a WhatIf over a table that already matches would leave nothing to change anyway.
        var json = $$"""
            {
                "Schema": "{{schema}}",
                "Name": "WhatIf",
                "Columns": [
                    {"Name": "Val", "DataType": "TEXT", "Nullable": true},
                    {"Name": "Id", "DataType": "INT", "Nullable": false},
                    {"Name": "Added", "DataType": "INT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, schema, "WhatIf", whatIf: true);

        Assert.That(Oid(cmd, schema, "WhatIf"), Is.EqualTo(oidBefore), "WhatIf must not replace the table.");
        Assert.That(Strings(cmd, deployedOrder), Is.EqualTo(new List<string> { "Id", "Val" }),
            "WhatIf must not add the declared column or reorder the deployed ones -- the shape must be "
            + "exactly what it was. This is what makes the preview safe to run against production.");
        Assert.That(Int(cmd, $@"SELECT COUNT(*) FROM ""{schema}"".""WhatIf"""), Is.EqualTo(2),
            "WhatIf must not move any rows.");
        Assert.That(Oid(cmd, schema, "WhatIf_SchemaSmithRebuild"), Is.EqualTo(-1),
            "WhatIf must not create the shadow table.");

        Assert.That(AuditCount(cmd, $"{schema}.WhatIf", "wouldRebuild"), Is.EqualTo(1),
            "The preview has to be visible in the run manifest, or a WhatIf report cannot show that a "
            + "rebuild was going to happen at all.");
        Assert.That(AuditCount(cmd, $"{schema}.WhatIf", "rebuilt"), Is.EqualTo(0),
            "A preview must never be recorded as work that happened.");

        Exec(cmd, $@"DROP SCHEMA ""{schema}"" CASCADE;");
        conn.Close();
    }

    // ---- privileges, owner and comments survive ----------------------------

    private static List<string> PrivilegesFor(IDbCommand cmd, string schema, string table, string role) => Strings(cmd, $@"
SELECT x.privilege_type || CASE WHEN x.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END || COALESCE(' (' || x.col || ')', '')
  FROM (SELECT (aclexplode(c.relacl)).*, NULL::text AS col FROM pg_class c WHERE c.oid = '""{schema}"".""{table}""'::regclass
        UNION ALL
        SELECT (aclexplode(a.attacl)).*, a.attname::text FROM pg_attribute a WHERE a.attrelid = '""{schema}"".""{table}""'::regclass AND a.attacl IS NOT NULL) x
 WHERE x.grantee = (SELECT oid FROM pg_roles WHERE rolname = '{role}')
 ORDER BY 1");

    private static string OwnerOf(IDbCommand cmd, string schema, string table) =>
        Scalar(cmd, $@"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = '""{schema}"".""{table}""'::regclass")?.ToString();

    private static string TableComment(IDbCommand cmd, string schema, string table) =>
        Scalar(cmd, $@"SELECT obj_description('""{schema}"".""{table}""'::regclass, 'pg_class')")?.ToString();

    private static string ColumnComment(IDbCommand cmd, string schema, string table, string column) =>
        Scalar(cmd, $@"SELECT col_description('""{schema}"".""{table}""'::regclass, (SELECT attnum FROM pg_attribute WHERE attrelid = '""{schema}"".""{table}""'::regclass AND attname = '{column}'))")?.ToString();

    private static int AuditRows(IDbCommand cmd, string schema, string table, string action) =>
        Int(cmd, $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""ObjectName"" LIKE '%{schema}%' AND ""ObjectName"" LIKE '%{table}%' AND ""ActionType"" = '{action}'");

    private static string TwoColumnJson(string schema, string table) => $$"""
        {
            "Schema": "{{schema}}",
            "Name": "{{table}}",
            "Columns": [
                {"Name": "Id", "DataType": "INT", "Nullable": false},
                {"Name": "Val", "DataType": "TEXT", "Nullable": true}
            ]
        }
        """;

    private static void CreateGrantedTable(IDbCommand cmd, string schema, string role)
    {
        Exec(cmd, $@"
CREATE ROLE ""{role}"" NOLOGIN;
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""KeepsGrants"" (""Id"" INT NOT NULL, ""Val"" TEXT NULL);
INSERT INTO ""{schema}"".""KeepsGrants"" (""Id"", ""Val"") VALUES (1, 'one');
GRANT SELECT ON ""{schema}"".""KeepsGrants"" TO ""{role}"";
GRANT INSERT ON ""{schema}"".""KeepsGrants"" TO ""{role}"" WITH GRANT OPTION;
GRANT UPDATE (""Val"") ON ""{schema}"".""KeepsGrants"" TO ""{role}"";
COMMENT ON TABLE ""{schema}"".""KeepsGrants"" IS 'kept table';
COMMENT ON COLUMN ""{schema}"".""KeepsGrants"".""Val"" IS 'kept column';");
    }

    private static void DropRoleAndSchema(IDbCommand cmd, string schema, string role)
    {
        try
        {
            Exec(cmd, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;");
            Exec(cmd, $@"DROP OWNED BY ""{role}""; DROP ROLE IF EXISTS ""{role}"";");
        }
        catch (NpgsqlException) { /* best-effort cleanup must not mask a test result */ }
    }

    [Test]
    public void Rebuild_KeepsPrivilegesAndComments_AndAuditsTheReapply()
    {
        var schema = NewSchema("RbPerm");
        var role = "rb_perm_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        try
        {
            CreateGrantedTable(cmd, schema, role);
            var before = PrivilegesFor(cmd, schema, "KeepsGrants", role);
            Assert.That(before, Has.Count.EqualTo(3), "setup: the three privileges must be on the table before the rebuild, or the comparison below is vacuous");
            var oidBefore = Oid(cmd, schema, "KeepsGrants");

            Rebuild(cmd, TwoColumnJson(schema, "KeepsGrants"), schema, "KeepsGrants");

            Assert.That(Oid(cmd, schema, "KeepsGrants"), Is.Not.EqualTo(oidBefore),
                "The table must actually have been replaced, or everything below passes on a procedure that did nothing.");
            Assert.Multiple(() =>
            {
                Assert.That(PrivilegesFor(cmd, schema, "KeepsGrants", role), Is.EqualTo(before),
                    "ALTER TABLE RENAME keeps the ACL on the renamed-out original, so the replacement starts with none: every "
                    + "privilege, grant option and column privilege has to be put back on it.");
                Assert.That(TableComment(cmd, schema, "KeepsGrants"), Is.EqualTo("kept table"), "the table comment is not in the package, so the rebuild has to carry it");
                Assert.That(ColumnComment(cmd, schema, "KeepsGrants", "Val"), Is.EqualTo("kept column"), "a column comment has to come across too");
                Assert.That(AuditRows(cmd, schema, "KeepsGrants", "reapplied"), Is.EqualTo(1), "The run manifest has to say the privileges were put back.");
            });
        }
        finally
        {
            DropRoleAndSchema(cmd, schema, role);
        }
    }

    [Test]
    public void Rebuild_KeepsTheOwner()
    {
        var schema = NewSchema("RbOwner");
        var role = "rb_owner_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        try
        {
            CreateGrantedTable(cmd, schema, role);
            Exec(cmd, $@"ALTER TABLE ""{schema}"".""KeepsGrants"" OWNER TO ""{role}"";");
            Assert.That(OwnerOf(cmd, schema, "KeepsGrants"), Is.EqualTo(role), "setup: the role must own the table before the rebuild");

            Rebuild(cmd, TwoColumnJson(schema, "KeepsGrants"), schema, "KeepsGrants");

            Assert.That(OwnerOf(cmd, schema, "KeepsGrants"), Is.EqualTo(role),
                "The shadow is created by the deploying role, so the original owner has to be put back on the replacement.");
        }
        finally
        {
            DropRoleAndSchema(cmd, schema, role);
        }
    }

    [Test]
    public void Rebuild_WhatIf_ReportsThePrivilegesItWouldReapply_AndTouchesNothing()
    {
        var schema = NewSchema("RbPermWhatIf");
        var role = "rb_perm_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        try
        {
            CreateGrantedTable(cmd, schema, role);
            var before = PrivilegesFor(cmd, schema, "KeepsGrants", role);
            var oidBefore = Oid(cmd, schema, "KeepsGrants");

            Rebuild(cmd, TwoColumnJson(schema, "KeepsGrants"), schema, "KeepsGrants", whatIf: true);

            Assert.Multiple(() =>
            {
                Assert.That(Oid(cmd, schema, "KeepsGrants"), Is.EqualTo(oidBefore), "WhatIf must not rebuild");
                Assert.That(PrivilegesFor(cmd, schema, "KeepsGrants", role), Is.EqualTo(before), "WhatIf must not touch a privilege");
                Assert.That(AuditRows(cmd, schema, "KeepsGrants", "wouldReapply"), Is.EqualTo(1),
                    "The preview lists the privileges the real run would put back, from the same capture.");
                Assert.That(AuditRows(cmd, schema, "KeepsGrants", "reapplied"), Is.EqualTo(0), "WhatIf records nothing as done");
            });
        }
        finally
        {
            DropRoleAndSchema(cmd, schema, role);
        }
    }
}
