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

    private static string GeneratedJson(Ctx ctx, string expression) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "tag", "DataType": "text", "Nullable": false },
                { "Name": "label", "DataType": "text", "Nullable": true, "Generated": "ALWAYS", "GenerationExpression": "{{expression}}" }
            ]
        }
        """;

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

    // Churns at the FLOOR (PostgreSQL 12) but not on 17, which is why an earlier pass of this work wrongly
    // concluded generated columns were already idempotent here -- the modern container cannot see it. The floor
    // sweep caught it. Runs on every supported version; only the floor legs exercise the difference.
    [Test]
    public void AGeneratedColumnAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        // The expression carries a literal, so PostgreSQL stores it with a ::text cast that no paren handling
        // reconciles -- the same rewrite the check-constraint case above turns on. An expression PostgreSQL
        // happens to store verbatim (qty * 2) would make this test pass without the mapping doing anything.
        var ctx = NewTable(@"""tag"" text NOT NULL");
        try
        {
            var json = GeneratedJson(ctx, @"upper(\""tag\"") || 'x'");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstAttNum = GeneratedColumnAttNum(ctx.Cmd, ctx.Table);
            var firstOid = TableOid(ctx.Cmd, ctx.Table);
            Assert.That(firstAttNum, Is.Not.Zero, "setup: the generated column must exist after the first deploy");
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
                                        SET ""EngineVersion"" = 'from-another-server', ""CanonicalText"" = 'stale text'
                                      WHERE ""ObjectTable"" = '{ctx.Table}'";
            Assert.That(ctx.Cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist to go stale");

            RunTableQuenchProc(ctx.Cmd, json);

            Assert.That(ConstraintOid(ctx.Cmd, ctx.Table), Is.EqualTo(firstOid),
                "a stale context must re-baseline, never re-apply");
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
