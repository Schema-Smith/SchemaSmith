// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Column defaults were compared after stripping one trailing type cast from each side. That re-set defaults PostgreSQL
/// stores in its own words on every deploy (<c>upper('n')</c> is <c>upper('n'::text)</c>, an operator default gains
/// parentheses) -- and it MISSED a real change: <c>now()</c> and <c>now()::date</c> both stripped to <c>now()</c>, so
/// the change was never applied. Defaults now go through the expression map like every other expression: compared
/// raw, vouched for by the map where the declared text is what produced the stored one. Asserted on what a user sees:
/// the stored default, the ChangeAudit rows, and the default's own catalog row (pg_attrdef), which a SET DEFAULT replaces.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ExpressionMapDefaultTests : BaseTableQuenchTests
{
    private static string Json(string t, string type, string dflt, bool extra = false) => $$"""
        [{ "Schema": "public", "Name": "{{t}}",
           "Columns": [
             { "Name": "id", "DataType": "integer", "Nullable": false },
             { "Name": "v", "DataType": "{{type}}", "Nullable": true, "Default": "{{dflt.Replace("\"", "\\\"")}}" }{{(extra ? """
           , { "Name": "w", "DataType": "varchar(10)", "Nullable": true, "Default": "'N'" }
           """ : "")}} ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string Stored(IDbCommand cmd, string t, string col = "v") => Scalar(cmd,
        $"SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{t}' AND column_name = '{col}'");

    private static string DefaultRow(IDbCommand cmd, string t, string col = "v") => Scalar(cmd, $"""
        SELECT d.oid::text FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
         WHERE d.adrelid = 'public."{t}"'::regclass AND a.attname = '{col}'
        """);

    private static string Audits(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT COALESCE(STRING_AGG("ObjectType" || ':' || "ActionType", ', ' ORDER BY "ObjectType", "ActionType"), '')
          FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{t}%' AND "ActionType" <> 'created'
        """);

    private static void ClearAudits(IDbCommand cmd) => Exec(cmd, """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""");

    private static int MapRows(IDbCommand cmd, string t) => Convert.ToInt32(Scalar(cmd,
        $"""SELECT COUNT(*) FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}' AND "Slot" = 'default'"""));

    private void WithTable(Action<IDbCommand, string, List<string>> body)
    {
        var t = $"dflt_{Guid.NewGuid():N}"[..20];
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

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [TestCase("varchar(10)", "'N'")]
    [TestCase("timestamptz", "now() + interval '1 day'")]
    [TestCase("date", "now()::date")]
    [TestCase("integer", "-1")]
    [TestCase("text", "upper('n')")]
    [TestCase("text[]", "ARRAY['a','b']")]
    [TestCase("boolean", "TRUE")]
    public void ADefault_IsCreatedOnce_AndLeftAlone(string type, string dflt)
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, type, dflt));
            Assert.That(Audits(cmd, t), Is.Empty, "the run that creates the table does not then re-set what it created");
            var row = DefaultRow(cmd, t);

            ClearAudits(cmd);
            messages.Clear();
            RunTableQuenchProc(cmd, Json(t, type, dflt));

            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "nothing re-applied on the second deploy");
                Assert.That(DefaultRow(cmd, t), Is.EqualTo(row), "the default was not re-set");
                Assert.That(messages, Has.None.Contains("on a scratch copy"), "the run that created it recorded it, so nothing is probed");
            });
        });
    }

    [Test]
    public void AColumnAddedWithADefault_IsNotReSetByTheRunThatAddsIt()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "integer", "-1"));
            ClearAudits(cmd);

            RunTableQuenchProc(cmd, Json(t, "integer", "-1", extra: true));

            Assert.That(Audits(cmd, t), Does.Not.Contain("modified"));
        });
    }

    [Test]
    public void AChangedDefault_IsApplied()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "varchar(10)", "'N'"));
            RunTableQuenchProc(cmd, Json(t, "varchar(10)", "'Y'"));
            Assert.That(Stored(cmd, t), Does.StartWith("'Y'"));
        });
    }

    [Test]
    public void ACastAddedToADefault_IsApplied_AndRemovedAgain()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "timestamptz", "now()"));
            Assert.That(Stored(cmd, t), Is.EqualTo("now()"), "setup");

            RunTableQuenchProc(cmd, Json(t, "timestamptz", "now()::date"));
            Assert.That(Stored(cmd, t), Does.Contain("::date"),
                "now() and now()::date both stripped to now(), so the change was never applied");

            RunTableQuenchProc(cmd, Json(t, "timestamptz", "now()"));
            Assert.That(Stored(cmd, t), Is.EqualTo("now()"));
        });
    }

    [Test]
    public void ADefaultChangedOutOfBand_IsPutBack()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "varchar(10)", "'N'"));
            RunTableQuenchProc(cmd, Json(t, "varchar(10)", "'N'"));
            Exec(cmd, $"""ALTER TABLE public."{t}" ALTER COLUMN v SET DEFAULT 'X'""");

            RunTableQuenchProc(cmd, Json(t, "varchar(10)", "'N'"));

            Assert.That(Stored(cmd, t), Does.StartWith("'N'"));
        });
    }

    [Test]
    public void ATableBuiltElsewhere_HasItsDefaultsRecorded_NotReSet()
    {
        WithTable((cmd, t, messages) =>
        {
            Exec(cmd, $"""CREATE TABLE public."{t}" (id INT NOT NULL, v TEXT DEFAULT upper('n'))""");
            var row = DefaultRow(cmd, t);

            RunTableQuenchProc(cmd, Json(t, "text", "upper('n')"));

            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty);
                Assert.That(DefaultRow(cmd, t), Is.EqualTo(row), "verified on a copy, not re-set");
                Assert.That(MapRows(cmd, t), Is.EqualTo(1));
            });
        });
    }

    [Test]
    public void APreview_OfATableBuiltElsewhere_LeavesTheMapAsItWas()
    {
        WithTable((cmd, t, messages) =>
        {
            Exec(cmd, $"""CREATE TABLE public."{t}" (id INT NOT NULL, v TEXT DEFAULT upper('n'))""");

            RunTableQuenchProc(cmd, Json(t, "text", "upper('n')"), whatIf: true);

            Assert.That(MapRows(cmd, t), Is.EqualTo(0));
        });
    }

    [Test]
    public void ATypeChange_ReRendersTheDefaultOnTheNewType()
    {
        WithTable((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Json(t, "varchar(10)", "'N'"));
            RunTableQuenchProc(cmd, Json(t, "text", "'N'"));
            Assert.That(Stored(cmd, t), Is.EqualTo("'N'::text"),
                "left alone, PostgreSQL keeps the default as it was stored on the old type: 'N'::character varying");
        });
    }

    [Test]
    public void AnExtractedDefault_DeploysAsItWas_AndThenChangesNothing()
    {
        WithTable((cmd, t, messages) =>
        {
            Exec(cmd, $"""CREATE TABLE public."{t}" (id INT NOT NULL, v TIMESTAMPTZ DEFAULT now() + interval '1 day', w TEXT DEFAULT upper('n'))""");
            var stored = Stored(cmd, t) + "|" + Stored(cmd, t, "w");
            var extracted = Scalar(cmd, $"""SELECT "SchemaSmith"."GenerateTableJSON"('public', '{t}')""");

            messages.Clear();
            RunTableQuenchProc(cmd, extracted);
            Assert.That(messages, Has.None.Contains("on a scratch copy"),
                "extracted as PostgreSQL stores it, the package needs nothing verified against the table it came from");

            Exec(cmd, $"""DROP TABLE public."{t}"; DELETE FROM "SchemaSmith"."ExpressionMap" WHERE "ObjectTable" = '{t}'""");

            RunTableQuenchProc(cmd, extracted);
            Assert.That(Stored(cmd, t) + "|" + Stored(cmd, t, "w"), Is.EqualTo(stored), "extracted: " + extracted);
            var row = DefaultRow(cmd, t);

            ClearAudits(cmd);
            RunTableQuenchProc(cmd, extracted);

            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty);
                Assert.That(DefaultRow(cmd, t), Is.EqualTo(row));
            });
        });
    }
}
