// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An exclusion constraint's keys and predicate were compared as text, and PostgreSQL stores <c>status = 'open'</c> on a
/// varchar column as <c>(status)::text = 'open'::text</c> -- so the constraint was dropped (CASCADE) and re-added on every
/// deploy. Both now go through the expression map. Asserted on the constraint's oid, which a re-add replaces.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ExpressionMapExcludeTests : BaseTableQuenchTests
{
    private static string Json(string t, string filter, string key = "room") => $$"""
        [{ "Schema": "public", "Name": "{{t}}",
           "Columns": [
             { "Name": "id", "DataType": "integer", "Nullable": false },
             { "Name": "room", "DataType": "varchar(20)", "Nullable": true },
             { "Name": "status", "DataType": "varchar(20)", "Nullable": true } ],
           "ExcludeConstraints": [ { "Name": "ex_{{t}}", "AccessMethod": "gist",
             "ExcludeColumns": [ { "Column": "{{key}}", "Operator": "=" } ], "FilterExpression": "{{filter}}" } ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string Oid(IDbCommand cmd, string t) =>
        Scalar(cmd, $"SELECT COALESCE((SELECT oid::text FROM pg_constraint WHERE conname = 'ex_{t}'), 'none')");

    private static string Definition(IDbCommand cmd, string t) =>
        Scalar(cmd, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ex_{t}'");

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
        var t = $"exm_{Guid.NewGuid():N}"[..20];
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var messages = new List<string>();
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        // Two fixtures creating the extension at once collide on its unique name; the loser finds it created.
        Exec(cmd, "DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS btree_gist; EXCEPTION WHEN unique_violation THEN NULL; END $$");
        void Drop() => Exec(cmd, $"""
            DROP TABLE IF EXISTS public."{t}"; DELETE FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}';
            DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid();
            """);
        Drop();
        try { body(cmd, t, messages); }
        finally { Drop(); }
    }

    private static void Clear(IDbCommand cmd, List<string> messages)
    {
        Exec(cmd, """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""");
        messages.Clear();
    }

    [TestCase("room", "status = 'open'")]
    [TestCase("lower(room)", "status = 'open'")]
    public void AnExclusion_IsCreatedOnce_AndLeftAlone(string key, string filter)
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, filter, key));
            var oid = Oid(cmd, t);
            Clear(cmd, messages);

            RunTableQuenchProc(cmd, Json(t, filter, key));

            Assert.Multiple(() =>
            {
                Assert.That(Oid(cmd, t), Is.EqualTo(oid), "the constraint was not dropped and re-added");
                Assert.That(Audits(cmd, t), Is.Empty);
                Assert.That(messages, Has.None.Contains("on a scratch copy"));
            });
        });
    }

    [Test]
    public void AChangedPredicate_IsApplied()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "status = 'open'"));
            RunTableQuenchProc(cmd, Json(t, "status = 'closed'"));
            Assert.That(Definition(cmd, t), Does.Contain("'closed'"));
        });
    }

    [Test]
    public void AChangedKeyExpression_IsApplied()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "status = 'open'", "lower(room)"));
            RunTableQuenchProc(cmd, Json(t, "status = 'open'", "upper(room)"));
            Assert.That(Definition(cmd, t), Does.Contain("upper("));
        });
    }

    [Test]
    public void AnExclusionBuiltElsewhere_IsRecorded_NotReAdded()
    {
        WithTable((cmd, t, messages) =>
        {
            Exec(cmd, $"""
                CREATE TABLE public."{t}" (id INT NOT NULL, room VARCHAR(20), status VARCHAR(20));
                ALTER TABLE public."{t}" ADD CONSTRAINT "ex_{t}" EXCLUDE USING gist (lower(room) WITH =) WHERE (status = 'open');
                """);
            var oid = Oid(cmd, t);

            RunTableQuenchProc(cmd, Json(t, "status = 'open'", "lower(room)"));

            Assert.Multiple(() =>
            {
                Assert.That(Oid(cmd, t), Is.EqualTo(oid), "verified on a copy, not re-added");
                Assert.That(Scalar(cmd, $"""SELECT COUNT(*) FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}' AND "ObjectKind" = 'EXCLUDE'"""), Is.EqualTo("2"));
            });
        });
    }
}
