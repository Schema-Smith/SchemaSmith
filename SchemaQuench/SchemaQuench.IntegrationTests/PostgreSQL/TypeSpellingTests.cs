// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Type spellings PostgreSQL reads back differently: a delimited collation ("C"), a sequence's type, and the real
/// changes the folds must not hide. A fold may only merge spellings of one type; these tests hold both directions.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TypeSpellingTests : BaseTableQuenchTests
{
    private static string Table(string t, string type, string collation = null) => $$"""
        [{ "Schema": "public", "Name": "{{t}}",
           "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false },
                        { "Name": "v", "DataType": "{{type}}", "Nullable": true{{(collation == null ? "" : $", \"Collation\": \"{collation.Replace("\"", "\\\"")}\"")}} } ] }]
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

    private void With(Action<IDbCommand, string, List<string>> body)
    {
        var t = $"spl_{Guid.NewGuid():N}"[..20];
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        var messages = new List<string>();
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Drop() => Exec(cmd, $"""DROP TABLE IF EXISTS public."{t}"; DROP SEQUENCE IF EXISTS public."{t}_s";""");
        Drop();
        try { body(cmd, t, messages); }
        finally { Drop(); }
    }

    private static string LiveType(IDbCommand cmd, string t) => Scalar(cmd,
        $"SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.\"{t}\"'::regclass AND attname = 'v'");

    private static string LiveCollation(IDbCommand cmd, string t) => Scalar(cmd,
        $"SELECT COALESCE(c.collname, '') FROM pg_attribute a LEFT JOIN pg_collation c ON c.oid = a.attcollation WHERE a.attrelid = 'public.\"{t}\"'::regclass AND a.attname = 'v'");

    [TestCase("\"C\"")]
    [TestCase("\"POSIX\"")]
    public void ADelimitedCatalogCollation_IsNotReAppliedOnRedeploy(string collation)
    {
        With((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Table(t, "text", collation));
            messages.Clear();
            RunTableQuenchProc(cmd, Table(t, "text", collation));
            Assert.That(messages.FindAll(m => m.Contains("Modified columns found") && m.Contains(t)), Is.Empty, string.Join(" | ", messages));
        });
    }

    [Test]
    public void AChangedCollation_IsStillApplied()
    {
        With((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Table(t, "text", "\"C\""));
            RunTableQuenchProc(cmd, Table(t, "text", "\"POSIX\""));
            Assert.That(LiveCollation(cmd, t), Is.EqualTo("POSIX"));
        });
    }

    [Test]
    public void AChangedScale_IsStillApplied()
    {
        With((cmd, t, messages) =>
        {
            RunTableQuenchProc(cmd, Table(t, "numeric(18,0)"));
            RunTableQuenchProc(cmd, Table(t, "numeric(18,2)"));
            Assert.That(LiveType(cmd, t), Is.EqualTo("numeric(18,2)"));
        });
    }

    private void Sequence(IDbCommand cmd, string t, string type) =>
        Exec(cmd, $"""CALL "SchemaSmith"."SequenceQuench"('{_productName}', '[{"{"} "Schema": "public", "Name": "{t}_s", "DataType": "{type}" {"}"}]', false)""");

    private static string SequenceType(IDbCommand cmd, string t) => Scalar(cmd,
        $"SELECT format_type(seqtypid, NULL) FROM pg_sequence WHERE seqrelid = 'public.\"{t}_s\"'::regclass");

    [TestCase("BIGINT")]
    [TestCase("int8")]
    [TestCase("INT")]
    public void ASequenceDeclaredWithATypeSynonym_IsNotAlteredOnRedeploy(string type)
    {
        With((cmd, t, messages) =>
        {
            Sequence(cmd, t, type);
            messages.Clear();
            Sequence(cmd, t, type);
            Assert.That(messages.FindAll(m => m.Contains("Altering sequence") || m.Contains("Modify sequence") || m.Contains(" AS ")), Is.Empty,
                string.Join(" | ", messages));
        });
    }

    [Test]
    public void AChangedSequenceType_IsStillApplied()
    {
        With((cmd, t, messages) =>
        {
            Sequence(cmd, t, "BIGINT");
            Sequence(cmd, t, "int");
            Assert.That(SequenceType(cmd, t), Is.EqualTo("integer"));
        });
    }

    // An extension type with a modifier compared as text: PostGIS writes the modifier in its own case and fills in a
    // geography's default SRID, so geometry(point,4326) or geography(Point) read as a type change on every deploy --
    // and the guard that protected the unmodified spelling skipped any declaration with a modifier at all.
    private static bool PostGis(IDbCommand cmd)
    {
        if (Scalar(cmd, "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'postgis'") == "0") return false;
        Exec(cmd, "DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS postgis; EXCEPTION WHEN unique_violation THEN NULL; END $$");
        return true;
    }

    [TestCase("geometry(Point,4326)")]
    [TestCase("geometry(point, 4326)")]
    [TestCase("public.geometry(POINT,4326)")]
    [TestCase("geography(Point)")]
    public void AnExtensionTypeWithAModifier_IsNotAlteredOnRedeploy(string type)
    {
        With((cmd, t, messages) =>
        {
            if (!PostGis(cmd)) Assert.Ignore("PostGIS is not available on this server.");
            RunTableQuenchProc(cmd, Table(t, type));
            messages.Clear();
            RunTableQuenchProc(cmd, Table(t, type));
            Assert.That(messages.FindAll(m => m.Contains("Modified columns found") && m.Contains(t)), Is.Empty, string.Join(" | ", messages));
        });
    }

    [Test]
    public void AChangedModifierOnAnExtensionType_IsStillApplied()
    {
        With((cmd, t, messages) =>
        {
            if (!PostGis(cmd)) Assert.Ignore("PostGIS is not available on this server.");
            RunTableQuenchProc(cmd, Table(t, "geometry(Point,4326)"));
            RunTableQuenchProc(cmd, Table(t, "geometry(Point,3857)"));
            Assert.That(LiveType(cmd, t), Is.EqualTo("geometry(Point,3857)"));
        });
    }
}
