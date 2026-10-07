// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Expression false-change detection (#242) on PostgreSQL — the twin of the SQL Server fixture.
/// <para><b>The measured case this exists for.</b> PostgreSQL rewrites literals when it stores an expression:
/// an authored <c>starts_with(tag, 'a')</c> comes back from <c>pg_get_constraintdef</c> as
/// <c>starts_with(tag, 'a'::text)</c>. No paren handling reconciles an added cast, so the constraint was
/// dropped and re-created on every deploy, forever. Generated columns are worse: their expression is compared
/// raw, and re-creating one rewrites the column.</para>
/// <para>Authored in natural form, asserted on identity (a constraint's oid, a column's attnum) rather than
/// text, and both directions covered — an out-of-band edit must still be re-applied.</para>
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ExpressionMapTests : BaseTableQuenchTests
{
    private sealed class Ctx : IDisposable
    {
        public NpgsqlConnection Conn = null!;
        public IDbCommand Cmd = null!;
        public string Table = null!;

        public void Drop()
        {
            Cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{Table}"";
                                 DELETE FROM ""SchemaSmith"".""ExpressionMap"" WHERE ""ObjectTable"" = '{Table}';";
            Cmd.ExecuteNonQuery();
            Conn.Close();
        }

        public void Dispose() => Conn.Dispose();
    }

    private Ctx NewTable(string columns)
    {
        var table = $"ExprMap_{Guid.NewGuid():N}"[..16];
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{table}"";
                             CREATE TABLE ""public"".""{table}"" ({columns});";
        cmd.ExecuteNonQuery();
        return new Ctx { Conn = conn, Cmd = cmd, Table = table };
    }

    private static string CheckJson(Ctx ctx, string expression) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "tag", "DataType": "text", "Nullable": true, "CheckExpression": "{{expression}}" }
            ]
        }
        """;

    private static string GeneratedJson(Ctx ctx, string expression, bool declareNullable = true) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "tag", "DataType": "text", "Nullable": false },
                { "Name": "label", "DataType": "text",{{(declareNullable ? " \"Nullable\": true," : "")}} "Generated": "ALWAYS", "GenerationExpression": "{{expression}}" }
            ]
        }
        """;

    private static string TableCheckJson(Ctx ctx, string expression) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "tag", "DataType": "text", "Nullable": true }
            ],
            "CheckConstraints": [
                { "Name": "ck_tag", "Expression": "{{expression}}" }
            ]
        }
        """;

    // GenerationExpression with no Generated -- what the reference's PostgreSQL column section tells you to write.
    private static string GeneratedWithoutGeneratedJson(Ctx ctx, string expression) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "tag", "DataType": "text", "Nullable": false },
                { "Name": "label", "DataType": "text", "Nullable": true, "GenerationExpression": "{{expression}}" }
            ]
        }
        """;

    private static bool LabelIsGenerated(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT a.attgenerated = 's' FROM pg_attribute a
                                               WHERE a.attrelid = to_regclass('""public"".""{table}""')
                                                 AND a.attname = 'label' AND NOT a.attisdropped), FALSE)";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static bool LabelIsNotNull(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT a.attnotnull FROM pg_attribute a
                                               WHERE a.attrelid = to_regclass('""public"".""{table}""')
                                                 AND a.attname = 'label' AND NOT a.attisdropped), FALSE)";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static long ConstraintOid(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT con.oid::bigint FROM pg_catalog.pg_constraint con
                                               WHERE con.conrelid = to_regclass('""public"".""{table}""')
                                                 AND con.contype = 'c' LIMIT 1), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string LiveCheckDefinition(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT pg_catalog.PG_GET_CONSTRAINTDEF(con.oid)
                                                FROM pg_catalog.pg_constraint con
                                               WHERE con.conrelid = to_regclass('""public"".""{table}""')
                                                 AND con.contype = 'c' LIMIT 1), '')";
        return cmd.ExecuteScalar() as string;
    }

    // A generated-column change on PostgreSQL is delivered as a table REBUILD -- build a replacement, copy,
    // swap -- which preserves attnum numbering, so attnum alone cannot see it. The table's own oid changes.
    private static long TableOid(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COALESCE(to_regclass('""public"".""{table}""')::oid::bigint, 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long GeneratedColumnAttNum(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT a.attnum::bigint FROM pg_attribute a
                                               WHERE a.attrelid = to_regclass('""public"".""{table}""')
                                                 AND a.attname = 'label' AND NOT a.attisdropped), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Two schemas with a same-named table: one declares a table-level check under the name the column form generates,
    // the other the column check itself. The column-check record was deduped on table and constraint name without the
    // schema, so the table-level row hid the other schema's column check and it compared raw on every deploy.
    [Test]
    public void TheSameCheckNameInTwoSchemas_IsRecordedForBoth()
    {
        var table = $"ExprMap2_{Guid.NewGuid():N}"[..16];
        const string otherSchema = "ss_exprmap_s2";
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $@"CREATE SCHEMA IF NOT EXISTS ""{otherSchema}"";";
            cmd.ExecuteNonQuery();
            var tableLevel = $$"""
                { "Schema": "public", "Name": "{{table}}",
                  "Columns": [ { "Name": "tag", "DataType": "text", "Nullable": true } ],
                  "CheckConstraints": [ { "Name": "CK_{{table}}_tag", "Expression": "starts_with(\"tag\", 'a')" } ] }
                """;
            var columnLevel = $$"""
                { "Schema": "{{otherSchema}}", "Name": "{{table}}",
                  "Columns": [ { "Name": "tag", "DataType": "text", "Nullable": true, "CheckExpression": "starts_with(\"tag\", 'a')" } ] }
                """;
            RunTableQuenchProc(cmd, "[" + tableLevel + "," + columnLevel + "]");

            cmd.CommandText = $@"SELECT COUNT(DISTINCT ""ObjectSchema"") FROM ""SchemaSmith"".""ExpressionMap""
                                 WHERE ""ObjectTable"" = '{table}' AND ""ObjectKind"" = 'CHECK';";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2));
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{table}""; DROP TABLE IF EXISTS ""{otherSchema}"".""{table}"";
                                 DELETE FROM ""SchemaSmith"".""ExpressionMap"" WHERE ""ObjectTable"" = '{table}';";
            cmd.ExecuteNonQuery();
        }
    }

    // The design's measured case: PostgreSQL adds ::text to the literal, and no paren handling reconciles a cast.
    [Test]
    public void ACheckWhoseLiteralTheEngineCasts_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = CheckJson(ctx, @"starts_with(\""tag\"", 'a')");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = ConstraintOid(ctx.Cmd, ctx.Table);
            Assert.That(firstOid, Is.Not.Zero, "setup: the constraint must exist after the first deploy");
            Assert.That(LiveCheckDefinition(ctx.Cmd, ctx.Table), Does.Contain("::text"),
                "precondition: this only tests something if the engine really did rewrite the literal");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(ConstraintOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid),
                    $"pass {pass}: the constraint was dropped and re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // An explicit NOT NULL arrives with the column, on the first deploy -- it was added nullable and narrowed a
    // deploy later, which also fails outright on a table whose rows make the expression NULL.
    [Test]
    public void AGeneratedColumnDeclaredNotNull_IsBuiltNotNull_OnTheFirstDeploy()
    {
        var ctx = NewTable(@"""tag"" text NOT NULL");
        try
        {
            var json = DeployJson.ThroughTheModel($$"""
                {
                    "Schema": "public",
                    "Name": "{{ctx.Table}}",
                    "Columns": [
                        { "Name": "tag", "DataType": "text", "Nullable": false },
                        { "Name": "label", "DataType": "text", "Nullable": false, "Generated": "ALWAYS", "GenerationExpression": "upper(tag) || 'x'" }
                    ]
                }
                """, Platform.PostgreSQL);
            RunTableQuenchProc(ctx.Cmd, json);
            Assert.That(LabelIsNotNull(ctx.Cmd, ctx.Table), Is.True, "the first deploy must build the declared NOT NULL");
            var firstOid = TableOid(ctx.Cmd, ctx.Table);
            var firstAttNum = GeneratedColumnAttNum(ctx.Cmd, ctx.Table);

            RunTableQuenchProc(ctx.Cmd, json);
            Assert.That(GeneratedColumnAttNum(ctx.Cmd, ctx.Table), Is.EqualTo(firstAttNum));
            Assert.That(TableOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid));
            Assert.That(LabelIsNotNull(ctx.Cmd, ctx.Table), Is.True);
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A generated column an older version narrowed to NOT NULL, Nullable omitted, whose expression changes to one that
    // yields NULL for an existing row. Below PostgreSQL 17 an expression change is DROP + re-ADD; re-emitting the old
    // NOT NULL on the re-add failed AFTER the drop, leaving the table without the column. Only a declared NOT NULL is
    // re-emitted now. PostgreSQL 17+ changes the expression in place, so this path does not exist there.
    [Test]
    public void AnExpressionChange_BelowPostgreSql17_NeverDropsAColumnItCannotReAdd()
    {
        var ctx = NewTable(@"""tag"" text NULL, ""label"" text GENERATED ALWAYS AS (upper(""tag"")) STORED NOT NULL");
        try
        {
            ctx.Cmd.CommandText = "SELECT current_setting('server_version_num')::int";
            if (Convert.ToInt32(ctx.Cmd.ExecuteScalar()) >= 170000)
                Assert.Ignore("PostgreSQL 17+ changes a generated expression in place; the drop + re-add path is below 17.");
            ctx.Cmd.CommandText = $@"INSERT INTO ""public"".""{ctx.Table}"" (tag) VALUES ('a');";
            ctx.Cmd.ExecuteNonQuery();

            var json = DeployJson.ThroughTheModel($$"""
                {
                    "Schema": "public",
                    "Name": "{{ctx.Table}}",
                    "Columns": [
                        { "Name": "tag", "DataType": "text", "Nullable": true },
                        { "Name": "label", "DataType": "text", "GenerationExpression": "nullif(upper(tag), 'A')" }
                    ]
                }
                """, Platform.PostgreSQL);

            RunTableQuenchProc(ctx.Cmd, json);

            Assert.That(GeneratedColumnAttNum(ctx.Cmd, ctx.Table), Is.Not.Zero, "the column must exist after the deploy");
            Assert.That(LabelIsNotNull(ctx.Cmd, ctx.Table), Is.False);
            ctx.Cmd.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{ctx.Table}"" WHERE label IS NULL";
            Assert.That(Convert.ToInt32(ctx.Cmd.ExecuteScalar()), Is.EqualTo(1), "the row whose new expression is NULL survives");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // The same rewrite through the table-level CheckConstraints array, which was compared by text alone: only the
    // column-level CheckExpression path consulted the mapping, so this churned on every deploy.
    [Test]
    public void ATableLevelCheckWhoseLiteralTheEngineCasts_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = TableCheckJson(ctx, "starts_with(tag, 'a')");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = ConstraintOid(ctx.Cmd, ctx.Table);
            Assert.That(firstOid, Is.Not.Zero, "setup: the constraint must exist after the first deploy");
            Assert.That(LiveCheckDefinition(ctx.Cmd, ctx.Table), Does.Contain("::text"),
                "precondition: this only tests something if the engine really did rewrite the literal");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(ConstraintOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid),
                    $"pass {pass}: the table-level check was dropped and re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // The WhatIf twin of the table-level drop pass must reach the same answer as the live pass: an unchanged check
    // the engine rewrote is not something a dry run would drop.
    [Test]
    public void AWhatIfRun_DoesNotReportAnUnchangedTableLevelCheck()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = TableCheckJson(ctx, "starts_with(tag, 'a')");
            RunTableQuenchProc(ctx.Cmd, json);
            ctx.Cmd.CommandText = @"DELETE FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""SessionId"" = pg_backend_pid()";
            ctx.Cmd.ExecuteNonQuery();

            RunTableQuenchProc(ctx.Cmd, json, whatIf: true);

            ctx.Cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                                     WHERE ""SessionId"" = pg_backend_pid() AND ""ActionType"" = 'wouldDrop'";
            Assert.That(Convert.ToInt32(ctx.Cmd.ExecuteScalar()), Is.Zero);
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // Quiet on churn must not mean blind to drift, on the table-level path too.
    [Test]
    public void AnOutOfBandEditToATableLevelCheck_IsStillReApplied()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = TableCheckJson(ctx, "starts_with(tag, 'a')");
            RunTableQuenchProc(ctx.Cmd, json);
            ctx.Cmd.CommandText = $@"ALTER TABLE ""public"".""{ctx.Table}"" DROP CONSTRAINT ck_tag;
                                     ALTER TABLE ""public"".""{ctx.Table}"" ADD CONSTRAINT ck_tag CHECK (starts_with(tag, 'z'));";
            ctx.Cmd.ExecuteNonQuery();

            RunTableQuenchProc(ctx.Cmd, json);

            Assert.That(LiveCheckDefinition(ctx.Cmd, ctx.Table), Does.Contain("'a'").And.Not.Contain("'z'"),
                "a hand-edited table-level check must be put back to the declaration");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A GenerationExpression is only ever a generated column on PostgreSQL, so omitting Generated must not change
    // what is built. It did: the create paths built a PLAIN column while the comparison treated it as generated,
    // and the next deploy failed with 55000 "... is not a stored generated column". Both create paths are covered
    // -- a new table, and a new column on an existing one.
    [TestCase(true)]
    [TestCase(false)]
    public void AGeneratedColumnDeclaredWithoutGenerated_IsBuiltGenerated_AndStaysPut(bool tableExistsFirst)
    {
        var ctx = NewTable(@"""tag"" text NOT NULL");
        try
        {
            if (!tableExistsFirst)
            {
                ctx.Cmd.CommandText = $@"DROP TABLE ""public"".""{ctx.Table}""";
                ctx.Cmd.ExecuteNonQuery();
            }
            var json = GeneratedWithoutGeneratedJson(ctx, "upper(tag) || 'x'");
            RunTableQuenchProc(ctx.Cmd, json);
            Assert.That(LabelIsGenerated(ctx.Cmd, ctx.Table), Is.True,
                "a column declared with a GenerationExpression must be built as a generated column");
            var firstOid = TableOid(ctx.Cmd, ctx.Table);
            var firstAttNum = GeneratedColumnAttNum(ctx.Cmd, ctx.Table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(GeneratedColumnAttNum(ctx.Cmd, ctx.Table), Is.EqualTo(firstAttNum),
                    $"pass {pass}: the generated column was re-created for an unchanged declaration");
                Assert.That(TableOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid),
                    $"pass {pass}: the table was rebuilt for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // Churns at the FLOOR (PostgreSQL 12) but not on 17, which is why an earlier pass of this work wrongly
    // concluded generated columns were already idempotent here -- the modern container cannot see it. The floor
    // sweep caught it. Runs on every supported version; only the floor legs exercise the difference.
    // declareNullable: false measures a generated column authored without "Nullable" -- on SQL Server that case
    // churned on every version because the create path and the comparison read the omission differently.
    [TestCase(true)]
    [TestCase(false)]
    public void AGeneratedColumnAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy(bool declareNullable)
    {
        // The expression carries a literal, so PostgreSQL stores it with a ::text cast that no paren handling
        // reconciles -- the same rewrite the check-constraint case above turns on. An expression PostgreSQL
        // happens to store verbatim (qty * 2) would make this test pass without the mapping doing anything.
        var ctx = NewTable(@"""tag"" text NOT NULL");
        try
        {
            var json = GeneratedJson(ctx, @"upper(\""tag\"") || 'x'", declareNullable);
            RunTableQuenchProc(ctx.Cmd, json);
            var firstAttNum = GeneratedColumnAttNum(ctx.Cmd, ctx.Table);
            var firstOid = TableOid(ctx.Cmd, ctx.Table);
            Assert.That(firstAttNum, Is.Not.Zero, "setup: the generated column must exist after the first deploy");
            // Declared nullable, or omitted -- which leaves nullability to the engine, and a generated column is
            // nullable unless asked otherwise. Pinned because a SET NOT NULL changes neither the attnum nor the table
            // oid below, so a narrowing deploy would otherwise pass as idempotent.
            Assert.That(LabelIsNotNull(ctx.Cmd, ctx.Table), Is.False,
                "a generated column the package did not declare NOT NULL must not be narrowed");
            ctx.Cmd.CommandText = $@"SELECT generation_expression FROM information_schema.columns
                                      WHERE table_schema = 'public' AND table_name = '{ctx.Table}' AND column_name = 'label'";
            Assert.That(ctx.Cmd.ExecuteScalar() as string, Does.Contain("::text"),
                "precondition: this only tests something if the engine really did rewrite the expression");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(GeneratedColumnAttNum(ctx.Cmd, ctx.Table), Is.EqualTo(firstAttNum),
                    $"pass {pass}: the generated column was re-created for an unchanged declaration");
                Assert.That(TableOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid),
                    $"pass {pass}: the table was rebuilt for an unchanged generated-column declaration");
                // The narrowing this guards against happened on a LATER pass, as SET NOT NULL -- invisible to both
                // checks above -- so it is asserted on every pass, not only the first.
                Assert.That(LabelIsNotNull(ctx.Cmd, ctx.Table), Is.False, $"pass {pass}: the column was narrowed to NOT NULL");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    [Test]
    public void AnOutOfBandEdit_IsStillReApplied()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = CheckJson(ctx, @"starts_with(\""tag\"", 'a')");
            RunTableQuenchProc(ctx.Cmd, json);

            ctx.Cmd.CommandText = $@"ALTER TABLE ""public"".""{ctx.Table}"" DROP CONSTRAINT ""CK_{ctx.Table}_tag"";
                                     ALTER TABLE ""public"".""{ctx.Table}"" ADD CONSTRAINT ""CK_{ctx.Table}_tag"" CHECK (starts_with(""tag"", 'zzz'));";
            ctx.Cmd.ExecuteNonQuery();

            RunTableQuenchProc(ctx.Cmd, json);

            Assert.That(LiveCheckDefinition(ctx.Cmd, ctx.Table), Does.Contain("'a'").And.Not.Contain("zzz"),
                "a hand-edited constraint must be re-applied, not silently accepted");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // Stale context AND drift together: the context must never be an excuse to stop reading the live object.
    // PostgreSQL's version string carries the packaging build, so a distro rebuild alone can move it.
    [Test]
    public void AStaleContextDoesNotExcuseDrift_TheObjectIsStillReApplied()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = CheckJson(ctx, @"starts_with(\""tag\"", 'a')");
            RunTableQuenchProc(ctx.Cmd, json);

            ctx.Cmd.CommandText = $@"ALTER TABLE ""public"".""{ctx.Table}"" DROP CONSTRAINT ""CK_{ctx.Table}_tag"";
                                     ALTER TABLE ""public"".""{ctx.Table}"" ADD CONSTRAINT ""CK_{ctx.Table}_tag"" CHECK (starts_with(tag, 'zzz'));";
            ctx.Cmd.ExecuteNonQuery();
            ctx.Cmd.CommandText = $@"UPDATE ""SchemaSmith"".""ExpressionMap"" SET ""EngineVersion"" = 'from-another-server'
                                      WHERE ""ObjectTable"" = '{ctx.Table}'";
            Assert.That(ctx.Cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist");

            RunTableQuenchProc(ctx.Cmd, json);

            Assert.That(LiveCheckDefinition(ctx.Cmd, ctx.Table), Does.Contain("'a'").And.Not.Contain("zzz"),
                "a version change must not excuse drift -- the declared expression must be re-applied");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    [Test]
    public void AStaleContextRow_IsReBaselined_WithoutTouchingTheObject()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            var json = CheckJson(ctx, @"starts_with(\""tag\"", 'a')");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = ConstraintOid(ctx.Cmd, ctx.Table);

            ctx.Cmd.CommandText = $@"UPDATE ""SchemaSmith"".""ExpressionMap""
                                        SET ""EngineVersion"" = 'from-another-server'
                                      WHERE ""ObjectTable"" = '{ctx.Table}'";
            Assert.That(ctx.Cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist to go stale (only the CONTEXT goes stale -- the recorded canonical text still matches the live object, which is what a real engine upgrade looks like)");

            var notices = new System.Collections.Generic.List<string>();
            ctx.Conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
            RunTableQuenchProc(ctx.Cmd, json);

            Assert.That(ConstraintOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid),
                "a stale context must re-baseline, never re-apply");
            Assert.That(notices.FindAll(n => n.Contains("Re-baselined 1 recorded expression")), Has.Count.EqualTo(1),
                "a re-baseline must be reported in the deploy log, not happen silently: "
                + string.Join(" | ", notices.FindAll(n => n.Contains("aseline"))));
            ctx.Cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ExpressionMap""
                                      WHERE ""ObjectTable"" = '{ctx.Table}' AND ""EngineVersion"" = 'from-another-server'";
            Assert.That(Convert.ToInt32(ctx.Cmd.ExecuteScalar()), Is.Zero,
                "the stale row must be rewritten with the current context");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    [Test]
    public void AChangedDeclaration_IsStillApplied()
    {
        var ctx = NewTable(@"""tag"" text NULL");
        try
        {
            RunTableQuenchProc(ctx.Cmd, CheckJson(ctx, @"starts_with(\""tag\"", 'a')"));
            RunTableQuenchProc(ctx.Cmd, CheckJson(ctx, @"starts_with(\""tag\"", 'b')"));

            Assert.That(LiveCheckDefinition(ctx.Cmd, ctx.Table), Does.Contain("'b'"),
                "an edited declaration must reach the server");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }
}
