// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Column types and collations that extraction used to write in a form that redeployed as something else, or not at
// all: interval precision and fields, "char", an array of a type in another schema, a collation in another schema,
// and a negative numeric scale (15+). The test is the user's round trip: extract a table, drop it, deploy the
// extracted definition, and require every column to come back with the same type and collation -- then deploy again
// and require nothing to be reported as modified.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class ColumnTypeRoundTripTests : BaseTableQuenchTests
{
    [Test]
    public void AnExtractedTable_RedeploysWithTheSameColumnTypesAndCollations()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var schema = $"ws04_{id}";
        var table = $"RoundTrip_{id}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "SELECT current_setting('server_version_num')::int";
        var negativeScale = Convert.ToInt32(cmd.ExecuteScalar()) >= 150000;

        try
        {
            cmd.CommandText = $"""
                CREATE SCHEMA "{schema}";
                CREATE TYPE "{schema}".e AS ENUM ('a', 'b');
                CREATE COLLATION "{schema}".c FROM "C";
                CREATE TABLE public."{table}" (
                    "Id" integer NOT NULL PRIMARY KEY,
                    i3 interval(3),
                    iym interval year to month,
                    ch "char",
                    ea "{schema}".e[],
                    cc text COLLATE "{schema}".c,
                    v varchar(20)
                    {(negativeScale ? ", n42 numeric(4,-2)" : "")}
                );
                """;
            cmd.ExecuteNonQuery();
            var before = Columns(cmd, table);

            cmd.CommandText = $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{table}')";
            var extracted = (string)cmd.ExecuteScalar()!;

            cmd.CommandText = $"DROP TABLE public.\"{table}\"";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, extracted);
            var after = Columns(cmd, table);
            Assert.That(after, Is.EquivalentTo(before),
                "every column must come back with the type and collation it was extracted from. Extracted: " + extracted);

            messages.Clear();
            RunTableQuenchProc(cmd, extracted);
            Assert.That(messages.Where(m => m.Contains("Modified columns found") && m.Contains(table)), Is.Empty,
                "a second deploy of the extracted definition must change nothing. Notices: " + string.Join(" | ", messages));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.\"{table}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    // A statistics object can live in a schema other than its table's. Extraction recorded only its name, so a
    // redeploy created it in the table's schema; and a change dropped it by the table's schema, which missed it, so
    // the recreate then failed because it still existed.
    [Test]
    public void AStatisticInAnotherSchema_RedeploysThereAndCanBeChanged()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var schema = $"ws04s_{id}";
        var table = $"StatHome_{id}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE public."{table}" ("Id" integer NOT NULL PRIMARY KEY, a integer, b integer);
                CREATE STATISTICS "{schema}".st (dependencies) ON a, b FROM public."{table}";
                """;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{table}')";
            var extracted = (string)cmd.ExecuteScalar()!;
            cmd.CommandText = $"DROP TABLE public.\"{table}\"";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, extracted);
            Assert.That(StatisticSchemaAndKind(cmd, table), Is.EqualTo($"{schema} f"),
                "the statistics object must be recreated in its own schema. Extracted: " + extracted);

            RunTableQuenchProc(cmd, extracted.Replace("\"DEPENDENCIES\"", "\"NDISTINCT\""));
            Assert.That(StatisticSchemaAndKind(cmd, table), Is.EqualTo($"{schema} d"),
                "a changed statistics object must be dropped from its own schema and recreated there");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.\"{table}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    private static string StatisticSchemaAndKind(System.Data.IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT string_agg(n.nspname || ' ' || array_to_string(se.stxkind, ','), ';')
              FROM pg_statistic_ext se JOIN pg_namespace n ON n.oid = se.stxnamespace
             WHERE se.stxrelid = 'public."{table}"'::regclass
            """;
        return cmd.ExecuteScalar() as string;
    }

    private static List<string> Columns(System.Data.IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod) || ' ' || COALESCE(co.collname, '')
              FROM pg_attribute a
              LEFT JOIN pg_collation co ON co.oid = a.attcollation AND a.attcollation <> 100
             WHERE a.attrelid = 'public."{table}"'::regclass AND a.attnum > 0 AND NOT a.attisdropped
            """;
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
