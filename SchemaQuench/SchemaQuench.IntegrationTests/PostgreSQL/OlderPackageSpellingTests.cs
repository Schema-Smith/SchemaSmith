// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Packages extracted by earlier releases spell some column types and collations differently from what extraction
// writes now: an extension type without its modifier (public.geometry), an array of a public type unqualified
// (e[]), a collation by its bare name. Deploying such a package over the database it came from must change nothing.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class OlderPackageSpellingTests : BaseTableQuenchTests
{
    [Test]
    public void APackageInTheOlderSpelling_ChangesNothingOnTheDatabaseItCameFrom()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"OldSpell_{id}";
        var enumType = $"e_{id}";
        var collation = $"ci_{id}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'postgis'";
        var hasPostgis = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

        try
        {
            if (hasPostgis)
            {
                cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis SCHEMA public";
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = $"""
                CREATE TYPE public."{enumType}" AS ENUM ('a', 'b');
                CREATE COLLATION public."{collation}" FROM "C";
                CREATE TABLE public."{table}" (
                    "Id" integer NOT NULL PRIMARY KEY,
                    ea public."{enumType}"[],
                    cc text COLLATE public."{collation}"
                    {(hasPostgis ? ", g public.geometry(Point,4326)" : "")}
                );
                """;
            cmd.ExecuteNonQuery();
            var before = Columns(cmd, table);

            cmd.CommandText = $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{table}')";
            var extracted = (string)cmd.ExecuteScalar()!;
            var older = Regex.Replace(extracted, @"public\.geometry\([^)]*\)", "public.geometry");
            older = older.Replace($"public.{enumType}[]", $"{enumType}[]")
                         .Replace($"public.\\\"{enumType}\\\"[]", $"{enumType}[]");
            older = Regex.Replace(older, $@"\\""public\\""\.\\""{collation}\\""", $@"\""{collation}\""");
            older = older.Replace($"public.{collation}", collation);
            Assert.That(older, Is.Not.EqualTo(extracted), "the test must actually deploy the older spellings. Extracted: " + extracted);

            messages.Clear();
            RunTableQuenchProc(cmd, older);
            var after = Columns(cmd, table);
            messages.Clear();
            RunTableQuenchProc(cmd, older);
            Assert.Multiple(() =>
            {
                Assert.That(after, Is.EquivalentTo(before), "every column must keep its type and collation. Deployed: " + older);
                Assert.That(messages.Where(m => m.Contains("Modified columns found") && m.Contains(table)), Is.Empty,
                    "a redeploy must report nothing as modified. Notices: " + string.Join(" | ", messages));
            });
        }
        finally
        {
            cmd.CommandText = $"""
                DROP TABLE IF EXISTS public."{table}";
                DROP TYPE IF EXISTS public."{enumType}";
                DROP COLLATION IF EXISTS public."{collation}";
                """;
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    // The older spellings are matched only to what the column already has: a different modifier, or a built-in type
    // declared without the length it has live, is still a change.
    [Test]
    public void ARealTypeChange_IsStillApplied()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"RealChange_{id}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'postgis'";
        var hasPostgis = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

        try
        {
            if (hasPostgis)
            {
                cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis SCHEMA public";
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = $"""
                CREATE TABLE public."{table}" (
                    "Id" integer NOT NULL PRIMARY KEY,
                    v varchar(20)
                    {(hasPostgis ? ", g public.geometry(Point,4326)" : "")}
                );
                """;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{table}')";
            var changed = ((string)cmd.ExecuteScalar()!).Replace("varchar(20)", "varchar").Replace("Point,4326", "Polygon,4326");

            RunTableQuenchProc(cmd, changed);
            var after = Columns(cmd, table);
            Assert.That(after, Does.Contain("v character varying default"), "an unbounded varchar declared over varchar(20) is a change");
            if (hasPostgis)
                Assert.That(after, Does.Contain("g geometry(Polygon,4326) "), "a different modifier is a change");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.\"{table}\"";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    private static List<string> Columns(System.Data.IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod) || ' ' || COALESCE(co.collname, '')
              FROM pg_attribute a
              LEFT JOIN pg_collation co ON co.oid = a.attcollation AND a.attcollation <> 0
             WHERE a.attrelid = 'public."{table}"'::regclass AND a.attnum > 0 AND NOT a.attisdropped
            """;
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
