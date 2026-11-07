// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An expression index key was compared as text, and PostgreSQL stores <c>lower(email)</c> on a varchar column as
/// <c>lower((email)::text)</c> -- so the index was dropped and rebuilt on every deploy. Index keys now go through the
/// expression map like partial-index predicates. Asserted on the index itself: its oid, which a rebuild replaces.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ExpressionMapIndexKeyTests : BaseTableQuenchTests
{
    private static string Json(string t, string keys, string ix = null) => $$"""
        [{ "Schema": "public", "Name": "{{t}}",
           "Columns": [
             { "Name": "id", "DataType": "integer", "Nullable": false },
             { "Name": "email", "DataType": "varchar(50)", "Nullable": true },
             { "Name": "n", "DataType": "integer", "Nullable": true } ],
           "Indexes": [ { "Name": "{{ix ?? "ix_" + t}}", "IndexColumns": "{{keys}}" } ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string IndexOid(IDbCommand cmd, string name) =>
        Scalar(cmd, $"""SELECT COALESCE(to_regclass('public."{name}"')::oid::text, 'none')""");

    private static string Keys(IDbCommand cmd, string name) =>
        Scalar(cmd, $"""SELECT pg_get_indexdef(to_regclass('public."{name}"'))""");

    private static string Audits(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT COALESCE(STRING_AGG("ObjectType" || ':' || "ActionType", ', ' ORDER BY "ObjectType", "ActionType"), '')
          FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{t}%'
        """);

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void WithTable(Action<IDbCommand, string, List<string>> body)
    {
        var t = $"ixk_{Guid.NewGuid():N}"[..20];
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var messages = new List<string>();
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Drop() => Exec(cmd, $"""
            DROP TABLE IF EXISTS public."{t}"; DELETE FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}';
            DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid();
            """);
        Drop();
        try { body(cmd, t, messages); }
        finally { Drop(); }
    }

    private void Clear(IDbCommand cmd, List<string> messages)
    {
        Exec(cmd, """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""");
        messages.Clear();
    }

    [TestCase("lower(email)")]
    [TestCase("(n * 2)")]
    [TestCase("lower(email), n")]
    public void AnExpressionIndex_IsCreatedOnce_AndLeftAlone(string keys)
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, keys));
            var oid = IndexOid(cmd, "ix_" + t);
            Clear(cmd, messages);

            RunTableQuenchProc(cmd, Json(t, keys));

            Assert.Multiple(() =>
            {
                Assert.That(IndexOid(cmd, "ix_" + t), Is.EqualTo(oid), "the index was not rebuilt");
                Assert.That(Audits(cmd, t), Is.Empty);
                Assert.That(messages, Has.None.Contains("on a scratch copy"), "the run that created it recorded it");
            });
        });
    }

    [Test]
    public void AnExpressionIndex_IsLeftAlone_ByAnIndexOnlyDeploy()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "lower(email)"));
            var oid = IndexOid(cmd, "ix_" + t);

            RunTableQuenchProc(cmd, Json(t, "lower(email)"), indexOnly: true);
            RunTableQuenchProc(cmd, Json(t, "lower(email)"), indexOnly: true);

            Assert.That(IndexOid(cmd, "ix_" + t), Is.EqualTo(oid));
        });
    }

    [Test]
    public void AChangedKeyExpression_IsApplied()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "lower(email)"));
            RunTableQuenchProc(cmd, Json(t, "upper(email)"));
            Assert.That(Keys(cmd, "ix_" + t), Does.Contain("upper("));
        });
    }

    [Test]
    public void AnIndexChangedOutOfBand_IsPutBack()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "lower(email)"));
            RunTableQuenchProc(cmd, Json(t, "lower(email)"));
            Exec(cmd, $"""DROP INDEX public."ix_{t}"; CREATE INDEX "ix_{t}" ON public."{t}" (upper(email))""");

            RunTableQuenchProc(cmd, Json(t, "lower(email)"));

            Assert.That(Keys(cmd, "ix_" + t), Does.Contain("lower("));
        });
    }

    [Test]
    public void AnIndexBuiltElsewhere_IsRecorded_NotRebuilt()
    {
        WithTable((cmd, t, messages) =>
        {
            Exec(cmd, $"""CREATE TABLE public."{t}" (id INT NOT NULL, email VARCHAR(50), n INT); CREATE INDEX "ix_{t}" ON public."{t}" (lower(email))""");
            var oid = IndexOid(cmd, "ix_" + t);

            RunTableQuenchProc(cmd, Json(t, "lower(email)"));

            Assert.Multiple(() =>
            {
                Assert.That(IndexOid(cmd, "ix_" + t), Is.EqualTo(oid), "verified on a copy, not rebuilt");
                Assert.That(Scalar(cmd, $"""SELECT COUNT(*) FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}' AND "Slot" = 'keys'"""), Is.EqualTo("1"));
            });
        });
    }

    [Test]
    public void ARenamedExpressionIndex_IsRenamed_NotRebuilt()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "lower(email)"));
            RunTableQuenchProc(cmd, Json(t, "lower(email)"));
            var oid = IndexOid(cmd, "ix_" + t);

            RunTableQuenchProc(cmd, Json(t, "lower(email)", ix: "ixr_" + t));

            Assert.That(IndexOid(cmd, "ixr_" + t), Is.EqualTo(oid));
        });
    }
}
