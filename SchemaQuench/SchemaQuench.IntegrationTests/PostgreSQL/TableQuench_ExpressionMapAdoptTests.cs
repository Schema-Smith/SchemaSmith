// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// #508 on PostgreSQL: a table another tool built holds its CHECK, generated column and partial-index predicate in
/// PostgreSQL's own words (<c>starts_with(tag, 'a'::text)</c>, <c>(status = ANY (ARRAY[1, 2]))</c>) and has no
/// expression-map rows, so the first deploy re-applied each one. The declared expression is now rendered on a scratch
/// copy first and recorded when PostgreSQL stores it as exactly the live text. Built by hand, declared in natural form,
/// asserted on identity: a constraint's and an index's oid, a column's attnum.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ExpressionMapAdoptTests : BaseTableQuenchTests
{
    private static string Create(string t, string check = "starts_with(tag, 'a')") => $"""
        CREATE TABLE public."{t}" (id INT NOT NULL, tag TEXT NULL, status INT NULL, code TEXT NULL,
          label TEXT GENERATED ALWAYS AS (tag || '-x') STORED,
          CONSTRAINT "ck_{t}" CHECK ({check}));
        CREATE UNIQUE INDEX "ux_{t}" ON public."{t}" (code) WHERE status IN (1, 2);
        """;

    private static string Json(string t, string check = "starts_with(tag, 'a')", string filter = "status IN (1, 2)") => $$"""
        [{ "Schema": "public", "Name": "{{t}}",
           "Columns": [
             { "Name": "id", "DataType": "integer", "Nullable": false },
             { "Name": "tag", "DataType": "text", "Nullable": true },
             { "Name": "status", "DataType": "integer", "Nullable": true },
             { "Name": "code", "DataType": "text", "Nullable": true },
             { "Name": "label", "DataType": "text", "Nullable": true, "Generated": "ALWAYS", "GenerationExpression": "tag || '-x'" } ],
           "Indexes": [ { "Name": "ux_{{t}}", "Unique": true, "IndexColumns": "code", "FilterExpression": "{{filter}}" } ],
           "CheckConstraints": [ { "Name": "ck_{{t}}", "Expression": "{{check}}" } ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string Identity(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT (SELECT oid FROM pg_constraint WHERE conname = 'ck_{t}')::text || '|' ||
               (SELECT attnum FROM pg_attribute WHERE attrelid = 'public."{t}"'::regclass AND attname = 'label' AND NOT attisdropped)::text || '|' ||
               COALESCE(to_regclass('public."ux_{t}"')::oid::text, 'none')
        """);

    private static string Audits(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT COALESCE(STRING_AGG("ObjectType" || ':' || "ActionType", ', ' ORDER BY "ObjectType", "ActionType"), '')
          FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{t}%'
        """);

    private static int MapRows(IDbCommand cmd, string t) => Convert.ToInt32(Scalar(cmd, $"""SELECT COUNT(*) FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}'"""));

    private void WithTable(string t, Action<IDbCommand, List<string>> body)
    {
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var messages = new List<string>();
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Drop()
        {
            cmd.CommandText = $"""
                DROP TABLE IF EXISTS public."{t}"; DELETE FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}';
                DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid();
                """;
            cmd.ExecuteNonQuery();
        }
        Drop();
        try { body(cmd, messages); }
        finally { Drop(); }
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void ATableBuiltElsewhere_HasNothingReApplied_OnItsFirstDeploy_AndTheSecondIsANoOp()
    {
        var t = $"adopt_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, messages) =>
        {
            Exec(cmd, Create(t));
            var before = Identity(cmd, t);

            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "nothing re-applied: the CHECK, generated column and partial index are the declared ones");
                Assert.That(Identity(cmd, t), Is.EqualTo(before), "the same constraint, column and index");
                Assert.That(messages, Has.Some.Contains("Verified 3 expression(s) on a scratch copy"));
                Assert.That(MapRows(cmd, t), Is.EqualTo(3));
            });

            messages.Clear();
            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Identity(cmd, t), Is.EqualTo(before), "the second deploy is a no-op");
                Assert.That(messages, Has.None.Contains("on a scratch copy"), "and the map answers, without a probe");
            });
        });
    }

    [Test]
    public void ARealDifference_IsStillReApplied()
    {
        var t = $"adoptdrift_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            Exec(cmd, Create(t, check: "starts_with(tag, 'b')"));
            var before = Identity(cmd, t).Split('|');

            RunTableQuenchProc(cmd, Json(t));
            var after = Identity(cmd, t).Split('|');
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_{t}'"), Does.Contain("'a'"), "the declared CHECK replaced the live one");
                Assert.That(after[0], Is.Not.EqualTo(before[0]), "re-created");
                Assert.That(after[1..], Is.EqualTo(before[1..]), "and only it: the generated column and the partial index stay");
            });
        });
    }

    [Test]
    public void AReformattedDeclaration_IsNotReApplied()
    {
        var t = $"adoptreword_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            RunTableQuenchProc(cmd, Json(t));
            var before = Identity(cmd, t);
            RunTableQuenchProc(cmd, Json(t, check: "STARTS_WITH( tag , 'a' )", filter: "status in (1,2)"));
            Assert.That(Identity(cmd, t), Is.EqualTo(before));
        });
    }

    [Test]
    public void WhatIf_ReportsNothingToDo_AndLeavesTheMapAsItWas()
    {
        var t = $"adoptwhatif_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            Exec(cmd, Create(t));
            RunTableQuenchProc(cmd, Json(t), whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "no would-change for an expression the database already holds");
                Assert.That(MapRows(cmd, t), Is.Zero, "the preview wrote nothing to the map");
            });
        });
    }

    // PG-080: the expression map re-baselines a version move only when the two texts are equal, which is right only if
    // PostgreSQL does not re-render these surfaces between releases. SP-5 measured that on 12.22 and 18.6; these are its
    // pairs, pinned, so the floor and the modern legs both fail if a release starts rendering them differently -- the
    // point at which the re-baselining rule has to be revisited.
    [Test]
    public void TheRenderingsTheExpressionMapReliesOn_AreTheOnesMeasured()
    {
        var t = $"render_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            Exec(cmd, $"""
                CREATE TABLE public."{t}" (a INT, n_int INT, s_plain VARCHAR(20), t_ts TIMESTAMP,
                  d_str VARCHAR(10) DEFAULT 'abc', d_neg INT DEFAULT -1, d_concat TEXT DEFAULT concat('A','B'), d_lower TEXT DEFAULT lower('ABC'),
                  d_sum INT DEFAULT 1 + 1, d_cast VARCHAR(10) DEFAULT CAST(1 AS VARCHAR(10)),
                  g_sum INT GENERATED ALWAYS AS (a + 1) STORED, g_lower TEXT GENERATED ALWAYS AS (lower(s_plain)) STORED,
                  CONSTRAINT "ck_in_{t}" CHECK (n_int IN (1,2,3)), CONSTRAINT "ck_between_{t}" CHECK (n_int BETWEEN 1 AND 10),
                  CONSTRAINT "ck_tz_{t}" CHECK (t_ts > timezone('UTC', '2020-01-01'::timestamp)));
                CREATE INDEX "ix_{t}" ON public."{t}" (a) WHERE n_int IS NOT NULL AND s_plain <> 'x';
                """);
            string Column(string c) => Scalar(cmd, $"SELECT COALESCE(generation_expression, column_default) FROM information_schema.columns WHERE table_name = '{t}' AND column_name = '{c}'");
            string Check(string c) => Scalar(cmd, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_{c}_{t}'");
            Assert.Multiple(() =>
            {
                Assert.That(Column("d_str"), Is.EqualTo("'abc'::character varying"));
                Assert.That(Column("d_neg"), Is.EqualTo("'-1'::integer"));
                Assert.That(Column("d_concat"), Is.EqualTo("concat('A', 'B')"));
                Assert.That(Column("d_lower"), Is.EqualTo("lower('ABC'::text)"));
                Assert.That(Column("d_sum"), Is.EqualTo("(1 + 1)"));
                Assert.That(Column("d_cast"), Is.EqualTo("(1)::character varying(10)"));
                Assert.That(Column("g_sum"), Is.EqualTo("(a + 1)"));
                Assert.That(Column("g_lower"), Is.EqualTo("lower((s_plain)::text)"));
                Assert.That(Check("in"), Is.EqualTo("CHECK ((n_int = ANY (ARRAY[1, 2, 3])))"));
                Assert.That(Check("between"), Is.EqualTo("CHECK (((n_int >= 1) AND (n_int <= 10)))"));
                Assert.That(Check("tz"), Is.EqualTo("CHECK ((t_ts > timezone('UTC'::text, '2020-01-01 00:00:00'::timestamp without time zone)))"));
                Assert.That(Scalar(cmd, $"SELECT pg_get_expr(indpred, indrelid) FROM pg_index WHERE indexrelid = 'public.\"ix_{t}\"'::regclass"),
                    Is.EqualTo("((n_int IS NOT NULL) AND ((s_plain)::text <> 'x'::text))"));
            });
        });
    }

    [Test]
    public void AnIndexOnlyDeploy_DoesNotRebuildAPartialIndexBuiltElsewhere()
    {
        var t = $"adoptidx_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            Exec(cmd, Create(t));
            var before = Identity(cmd, t);
            RunTableQuenchProc(cmd, Json(t), indexOnly: true);
            Assert.That(Identity(cmd, t), Is.EqualTo(before));
        });
    }
}
