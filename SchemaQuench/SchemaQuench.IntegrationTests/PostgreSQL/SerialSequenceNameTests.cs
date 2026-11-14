// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A serial column's sequence was named table_column_seq, ignoring the 63-byte limit and the engine's collision
/// suffix: an integer column converted to serial got a sequence named differently from the one a fresh serial gets,
/// so extraction and redeploy disagreed; and the run that created a table with a long-named serial column set its
/// default again, because the default was worked out before the table existed. The name now follows the engine's.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class SerialSequenceNameTests : BaseTableQuenchTests
{
    private const string LongColumn = "a_rather_long_identifier_column_name";

    private static string Json(string schema, string t, string type, string col = LongColumn) => $$"""
        [{ "Schema": "{{schema}}", "Name": "{{t}}",
           "Columns": [ { "Name": "{{col}}", "DataType": "{{type}}", "Nullable": false }, { "Name": "v", "DataType": "text", "Nullable": true } ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Modified(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT COUNT(*) FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{t}%' AND "ActionType" = 'modified'
        """);

    private static string OwnedSequence(IDbCommand cmd, string schema, string t, string col = LongColumn) => Scalar(cmd,
        $"SELECT c.relname FROM pg_class c WHERE c.oid = pg_get_serial_sequence('\"{schema}\".\"{t}\"', '{col}')::regclass");

    // The name the engine itself gives a fresh serial column of this table and column, in a scratch schema.
    private static string EngineName(IDbCommand cmd, string t, string col = LongColumn, string preexisting = null)
    {
        var scratch = $"ssn_{Guid.NewGuid():N}"[..12];
        Exec(cmd, $"""CREATE SCHEMA "{scratch}";""" + (preexisting == null ? "" : $"""CREATE SEQUENCE "{scratch}"."{preexisting}";""") +
                  $"""CREATE TABLE "{scratch}"."{t}" ("{col}" SERIAL);""");
        try { return OwnedSequence(cmd, scratch, t, col); }
        finally { Exec(cmd, $"""DROP SCHEMA "{scratch}" CASCADE;"""); }
    }

    private void With(Action<IDbCommand, string, string> body)
    {
        var schema = $"ssn_{Guid.NewGuid():N}"[..12];
        // 60 characters: with the column, past the 63-byte budget, so the engine trims the longer part first.
        var t = ("serial_table_" + Guid.NewGuid().ToString("N") + "xxxxxxxxxxxxxxxxxxxx")[..60];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Exec(cmd, $"""CREATE SCHEMA "{schema}"; DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid();""");
        try { body(cmd, schema, t); }
        finally { Exec(cmd, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE;"""); }
    }

    [Test]
    public void ALongNamedSerialColumn_IsNotReSetByTheRunThatCreatesItsTable()
    {
        With((cmd, schema, t) =>
        {
            RunTableQuenchProc(cmd, Json(schema, t, "serial"));
            Assert.That(Modified(cmd, t), Is.EqualTo("0"), "the creating run set the default it had just created");

            RunTableQuenchProc(cmd, Json(schema, t, "serial"));
            Assert.That(Modified(cmd, t), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AColumnConvertedToSerial_GetsTheSequenceAFreshSerialGets_AndThenChangesNothing()
    {
        With((cmd, schema, t) =>
        {
            RunTableQuenchProc(cmd, Json(schema, t, "integer"));
            RunTableQuenchProc(cmd, Json(schema, t, "serial"));
            Assert.That(OwnedSequence(cmd, schema, t), Is.EqualTo(EngineName(cmd, t)));

            Exec(cmd, """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""");
            RunTableQuenchProc(cmd, Json(schema, t, "serial"));
            Assert.That(Modified(cmd, t), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AColumnConvertedToSerial_WhereTheNameIsTaken_GetsTheEngineSuffix()
    {
        With((cmd, schema, t) =>
        {
            const string col = "c";
            var name = EngineName(cmd, t, col);
            Exec(cmd, $"""CREATE SEQUENCE "{schema}"."{name}";""");
            RunTableQuenchProc(cmd, Json(schema, t, "integer", col));

            RunTableQuenchProc(cmd, Json(schema, t, "serial", col));

            Assert.That(OwnedSequence(cmd, schema, t, col), Is.EqualTo(EngineName(cmd, t, col, preexisting: name)));
            Assert.That(OwnedSequence(cmd, schema, t, col), Is.Not.EqualTo(name), "the name was taken, so the engine suffixes it");
        });
    }

    [Test]
    public void AConversionAnEarlierRunLeftHalfDone_UsesTheSequenceTheColumnAlreadyOwns()
    {
        With((cmd, schema, t) =>
        {
            const string col = "c";
            RunTableQuenchProc(cmd, Json(schema, t, "integer", col));
            var name = EngineName(cmd, t, col);
            // What a run that stopped after CREATE SEQUENCE ... OWNED BY and before the ALTER leaves behind.
            Exec(cmd, $"""CREATE SEQUENCE "{schema}"."{name}" OWNED BY "{schema}"."{t}"."{col}";""");

            RunTableQuenchProc(cmd, Json(schema, t, "serial", col));

            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM pg_class WHERE relkind = 'S' AND relnamespace = '\"{schema}\"'::regnamespace"), Is.EqualTo("1"),
                    "no second sequence beside the one the column owns");
                Assert.That(OwnedSequence(cmd, schema, t, col), Is.EqualTo(name));
                Assert.That(Scalar(cmd, $"SELECT column_default FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{t}' AND column_name = '{col}'"),
                    Does.Contain(name));
            });
        });
    }

    // In a schema on the search_path the catalog renders the default unqualified -- nextval('t_c_seq'::regclass) --
    // which the parse, writing it before the table existed, could not know.
    [Test]
    public void ASerialColumnInASchemaOnTheSearchPath_IsNotReSetByTheRunThatCreatesItsTable()
    {
        var t = $"serial_pub_{Guid.NewGuid():N}"[..24];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Exec(cmd, """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid();""");
        try
        {
            RunTableQuenchProc(cmd, Json("public", t, "serial", "c"));
            Assert.That(Modified(cmd, t), Is.EqualTo("0"), "the creating run set the default it had just created");
        }
        finally { Exec(cmd, $"""DROP TABLE IF EXISTS public."{t}";"""); }
    }
}
