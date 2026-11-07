// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// G1: PostgreSQL declarative partitioning, adopt and verify. Assertions are on outcomes -- where rows land, what the
// catalog holds, what a second deploy records -- never on the DDL text.
[Category("PostgreSQL")]
[NonParallelizable]
public class TableQuench_DeclarativePartitioningTests : BaseTableQuenchTests
{
    private NpgsqlConnection Open()
    {
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static void Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    private static List<string> Changes(IDbConnection conn)
    {
        var rows = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"ObjectType\" || ' ' || \"ObjectName\" || ' ' || \"ActionType\" FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() ORDER BY 1";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    private static void ClearChanges(IDbConnection conn) => Exec(conn, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()");

    // A LIST-partitioned table whose eu partition is RANGE-partitioned again on a timestamptz (one bound written without an
    // offset, one from MINVALUE), a DEFAULT partition, and a HASH-partitioned table beside it.
    private static string Json(string t, bool with2027 = false) => $$"""
        [{ "Schema": "public", "Name": "{{t}}",
           "Columns": [ { "Name": "region", "DataType": "text", "Nullable": false },
                        { "Name": "created_at", "DataType": "timestamp with time zone", "Nullable": false },
                        { "Name": "id", "DataType": "bigint", "Nullable": false } ],
           "Indexes": [ { "Name": "pk_{{t}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "region,created_at,id" },
                        { "Name": "ix_{{t}}_id", "IndexColumns": "id" } ],
           "Partitioning": { "Method": "LIST", "Expression": "region", "Partitions": [
             { "Name": "{{t}}_eu", "Bound": "FOR VALUES IN ('eu')",
               "Partitioning": { "Method": "RANGE", "Expression": "created_at", "Partitions": [
                 { "Name": "{{t}}_eu_old", "Bound": "FOR VALUES FROM (MINVALUE) TO ('2026-01-01')" },
                 { "Name": "{{t}}_eu_2026", "Bound": "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01')" }{{(with2027 ? $", {{ \"Name\": \"{t}_eu_2027\", \"Bound\": \"FOR VALUES FROM ('2027-01-01') TO ('2028-01-01')\" }}" : "")}} ] } },
             { "Name": "{{t}}_rest", "Bound": "DEFAULT" } ] } },
         { "Schema": "public", "Name": "{{t}}_h",
           "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
           "Partitioning": { "Method": "HASH", "Expression": "id", "Partitions": [
             { "Name": "{{t}}_h0", "Bound": "FOR VALUES WITH (modulus 2, remainder 0)" },
             { "Name": "{{t}}_h1", "Bound": "FOR VALUES WITH (modulus 2, remainder 1)" } ] } }]
        """;

    private static void Drop(IDbConnection conn, string t) => Exec(conn, $"DROP TABLE IF EXISTS public.{t} CASCADE; DROP TABLE IF EXISTS public.{t}_h CASCADE;");

    [Test]
    public void ADeclaredPartitionTree_IsCreated_RowsLandWhereTheBoundsSay_AndTheSecondDeployChangesNothing()
    {
        var t = "dp_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = Open();
        try
        {
            // Deployed from a session far from UTC: the bounds still mean what extraction would write, in UTC.
            Exec(conn, "SET TimeZone = 'America/Chicago'");
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t), productName: "Dp_" + t);

            Exec(conn, $"INSERT INTO public.{t} VALUES ('eu', '2025-06-01 00:00:00+00', 1), ('eu', '2026-01-01 00:00:00+00', 2), ('us', '2026-03-01 00:00:00+00', 3); " +
                       $"INSERT INTO public.{t}_h SELECT g FROM generate_series(1, 20) g;");
            string Where(long id) => Scalar(conn, $"SELECT tableoid::regclass::text FROM public.{t} WHERE id = {id}");
            Assert.Multiple(() =>
            {
                Assert.That(Where(1), Is.EqualTo($"{t}_eu_old"), "below the MINVALUE partition's upper bound");
                Assert.That(Where(2), Is.EqualTo($"{t}_eu_2026"), "an offset-less bound means UTC midnight");
                Assert.That(Where(3), Is.EqualTo($"{t}_rest"), "an unmatched row lands in DEFAULT");
                Assert.That(Scalar(conn, $"SELECT COUNT(DISTINCT tableoid) FROM public.{t}_h"), Is.EqualTo("2"), "HASH spreads rows over both partitions");
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM pg_index WHERE indrelid = 'public.{t}_eu_2026'::regclass"), Is.EqualTo("2"), "the parent's PK and index reach a leaf");
            });

            ClearChanges(conn);
            Exec(conn, "SET TimeZone = 'America/Chicago'");
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t), productName: "Dp_" + t);
            Assert.That(Changes(conn), Is.Empty, "an unchanged package changes nothing, whatever the session's time zone");
        }
        finally
        {
            Exec(conn, "RESET TimeZone");
            Drop(conn, t);
        }
    }

    [Test]
    public void ANewlyDeclaredPartition_IsAppended_AndAPreviewCreatesNothing()
    {
        var t = "dpa_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = Open();
        try
        {
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t), productName: "Dpa_" + t);
            Exec(conn, $"INSERT INTO public.{t} VALUES ('eu', '2026-06-01 00:00:00+00', 1)");

            ClearChanges(conn);
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t, with2027: true), whatIf: true, productName: "Dpa_" + t);
            Assert.Multiple(() =>
            {
                Assert.That(Changes(conn), Is.EqualTo(new[] { $"partition public.{t}_eu.{t}_eu_2027 wouldCreate" }));
                Assert.That(Scalar(conn, $"SELECT to_regclass('public.{t}_eu_2027') IS NULL"), Is.EqualTo("True"), "a preview creates nothing");
            });

            ClearChanges(conn);
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t, with2027: true), productName: "Dpa_" + t);
            Assert.Multiple(() =>
            {
                Assert.That(Changes(conn), Is.EqualTo(new[] { $"partition public.{t}_eu.{t}_eu_2027 created" }));
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM public.{t}"), Is.EqualTo("1"), "no row moved");
            });
        }
        finally
        {
            Drop(conn, t);
        }
    }

    private Exception DeployFails(IDbConnection conn, string json, string product, bool whatIf = false)
    {
        using var cmd = conn.CreateCommand();
        return Assert.Catch(() => RunTableQuenchProc(cmd, json, whatIf: whatIf, productName: product));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AChangedBoundOrKey_IsRefusedByName_BeforeAnyChange(bool whatIf)
    {
        var t = "dpv_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = Open();
        try
        {
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t), productName: "Dpv_" + t);

            ClearChanges(conn);
            var bound = DeployFails(conn, Json(t).Replace("TO ('2027-01-01')", "TO ('2027-07-01')"), "Dpv_" + t, whatIf);
            Assert.Multiple(() =>
            {
                Assert.That(bound.Message, Does.Contain($"Partition public.{t}_eu_2026 of public.{t}_eu declares").And.Contain("2027-07-01").And.Contain("does not change a partition's bound"));
                Assert.That(Changes(conn), Is.Empty, "nothing changed");
            });

            var key = DeployFails(conn, Json(t).Replace("\"Method\": \"HASH\", \"Expression\": \"id\"", "\"Method\": \"HASH\", \"Expression\": \"(id + 1)\""), "Dpv_" + t, whatIf);
            Assert.That(key.Message, Does.Contain($"Table public.{t}_h declares PARTITION BY HASH ((id + 1)), but is deployed as PARTITION BY HASH (id)"));
        }
        finally
        {
            Drop(conn, t);
        }
    }

    [Test]
    public void ADeclaredPartitionThatExistsAsAStandaloneTable_IsRefused()
    {
        var t = "dps_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = Open();
        try
        {
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t), productName: "Dps_" + t);
            Exec(conn, $"CREATE TABLE public.{t}_eu_2027 (region text NOT NULL, created_at timestamptz NOT NULL, id bigint NOT NULL)");
            var ex = DeployFails(conn, Json(t, with2027: true), "Dps_" + t);
            Assert.That(ex.Message, Does.Contain($"public.{t}_eu_2027 already exists and is not a partition of it (a standalone table)"));
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{t}_eu_2027");
            Drop(conn, t);
        }
    }

    [Test]
    public void ANewPartitionWhoseRangeHoldsDefaultRows_IsRefused_AndNothingIsCreated()
    {
        var t = "dpd_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = Open();
        try
        {
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t), productName: "Dpd_" + t);
            // A row for a region the package does not partition yet lands in DEFAULT; declaring that region's partition would move it.
            Exec(conn, $"INSERT INTO public.{t} VALUES ('apac', '2026-03-01 00:00:00+00', 7)");
            var withApac = Json(t).Replace($"{{ \"Name\": \"{t}_rest\", \"Bound\": \"DEFAULT\" }}",
                $"{{ \"Name\": \"{t}_apac\", \"Bound\": \"FOR VALUES IN ('apac')\" }}, {{ \"Name\": \"{t}_rest\", \"Bound\": \"DEFAULT\" }}");
            Assert.That(withApac, Does.Contain($"{t}_apac"), "setup: the declaration changed");
            var ex = DeployFails(conn, withApac, "Dpd_" + t);
            Assert.Multiple(() =>
            {
                Assert.That(ex.Message, Does.Contain($"Partition public.{t}_apac cannot be added to public.{t}: its DEFAULT partition").And.Contain("holds rows inside the bound"));
                Assert.That(Scalar(conn, $"SELECT to_regclass('public.{t}_apac') IS NULL"), Is.EqualTo("True"));
                Assert.That(Scalar(conn, $"SELECT tableoid::regclass::text FROM public.{t} WHERE id = 7"), Is.EqualTo($"{t}_rest"), "the row stayed where it was");
            });
        }
        finally
        {
            Drop(conn, t);
        }
    }

    [Test]
    public void AnUndeclaredPartition_IsLeftWithItsRows()
    {
        var t = "dpu_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = Open();
        try
        {
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, Json(t, with2027: true), productName: "Dpu_" + t);
            Exec(conn, $"INSERT INTO public.{t} VALUES ('eu', '2027-03-01 00:00:00+00', 9)");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(t), productName: "Dpu_" + t), "a partition dropped from the declaration is not a refusal");
            Assert.That(Scalar(conn, $"SELECT tableoid::regclass::text FROM public.{t} WHERE id = 9"), Is.EqualTo($"{t}_eu_2027"), "still there, with its row");
        }
        finally
        {
            Drop(conn, t);
        }
    }

    // A package that declared a hand-made partition as a table of its own, then declares it under its parent's
    // Partitioning: the partition is no longer a table the product owns, so it is not dropped by absence (which the
    // data-loss guard would refuse on every deploy) and its ownership row goes.
    [Test]
    public void APartitionOnceDeclaredAsATable_MovesUnderPartitioning_WithoutADrop()
    {
        var t = "dpo_" + Guid.NewGuid().ToString("N")[..8];
        var product = "Dpo_" + t;
        using var conn = Open();
        try
        {
            Exec(conn, $"CREATE TABLE public.{t} (id integer NOT NULL, d date NOT NULL) PARTITION BY RANGE (d); " +
                       $"CREATE TABLE public.{t}_2026 PARTITION OF public.{t} FOR VALUES FROM ('2026-01-01') TO ('2027-01-01'); " +
                       $"INSERT INTO public.{t} VALUES (1, '2026-05-01');");
            var asTable = $$"""
                [{ "Schema": "public", "Name": "{{t}}_2026",
                   "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "d", "DataType": "date", "Nullable": false } ] }]
                """;
            using (var cmd = conn.CreateCommand())
                RunTableQuenchProc(cmd, asTable, productName: product);
            Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}' AND \"TableName\" = '{t}_2026' AND \"IndexName\" IS NULL"),
                Is.EqualTo("1"), "setup: owned as a table");

            var asPartition = $$"""
                [{ "Schema": "public", "Name": "{{t}}",
                   "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "d", "DataType": "date", "Nullable": false } ],
                   "Partitioning": { "Method": "RANGE", "Expression": "d", "Partitions": [
                     { "Name": "{{t}}_2026", "Bound": "FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')" } ] } }]
                """;
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, asPartition, dropTablesRemovedFromProduct: true, productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM public.{t}_2026"), Is.EqualTo("1"), "the partition and its row are still there");
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}' AND \"TableName\" = '{t}_2026'"),
                    Is.EqualTo("0"), "it is owned through its parent now");
            });
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{t} CASCADE");
            Exec(conn, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }
}
