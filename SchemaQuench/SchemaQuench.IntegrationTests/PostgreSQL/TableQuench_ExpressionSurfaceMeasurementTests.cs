// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// #242, the remaining expression surfaces on PostgreSQL: partial-index filters, column defaults and exclude
/// constraints.
/// <para>Measurement first, wiring second. Two surfaces the design listed turned out to be idempotent already,
/// and a release note claiming otherwise would be false — so each case is authored in natural form and asserts
/// the object is left alone. Whichever reddens is a real defect.</para>
/// <para>Identity, not text: an index and a constraint each keep their oid unless they were re-created.</para>
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ExpressionSurfaceMeasurementTests : BaseTableQuenchTests
{
    private sealed class Ctx : IDisposable
    {
        public NpgsqlConnection Conn = null!;
        public IDbCommand Cmd = null!;
        public string Table = null!;

        public void Drop()
        {
            Cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{Table}"" CASCADE;
                                 DELETE FROM ""SchemaSmith"".""ExpressionMap"" WHERE ""ObjectTable"" = '{Table}';";
            Cmd.ExecuteNonQuery();
            Conn.Close();
        }

        public void Dispose() => Conn.Dispose();
    }

    private Ctx NewTable(string columns)
    {
        var table = $"ExprSurf_{Guid.NewGuid():N}"[..16];
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{table}"" CASCADE;
                             CREATE TABLE ""public"".""{table}"" ({columns});";
        cmd.ExecuteNonQuery();
        return new Ctx { Conn = conn, Cmd = cmd, Table = table };
    }

    private static long IndexOid(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT c.oid::bigint FROM pg_class c
                                               WHERE c.relname = '{index}' AND c.relkind = 'i'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string LiveDefault(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT column_default FROM information_schema.columns
                                               WHERE table_schema = 'public' AND table_name = '{table}'
                                                 AND column_name = '{column}'), '')";
        return cmd.ExecuteScalar() as string;
    }

    private static string FilteredIndexJson(Ctx ctx, string filter) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "status", "DataType": "integer", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "IX_{{ctx.Table}}_status", "IndexColumns": "status", "FilterExpression": "{{filter}}" }
            ]
        }
        """;

    private static string DefaultJson(Ctx ctx, string defaultValue) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "tag", "DataType": "text", "Nullable": true, "Default": "{{defaultValue}}" }
            ]
        }
        """;

    // A partial index authored the way a person writes it; PostgreSQL stores the predicate its own way.
    // The column list is authored UNQUOTED to match what the catalog reports: a quoted list churns for its own
    // reason (declared "status" vs live status), which has nothing to do with the predicate and would make
    // this test pass or fail for the wrong reason.
    [Test]
    public void APartialIndexAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = FilteredIndexJson(ctx, @"\""status\"" > 0");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status");
            Assert.That(firstOid, Is.Not.Zero, "setup: the partial index must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status"), Is.EqualTo(firstOid),
                    $"pass {pass}: the partial index was re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A text default: PostgreSQL stores 'none'::text, which the type-cast stripper is meant to reconcile.
    [Test]
    public void ATextDefaultAuthoredInNaturalForm_IsNotReAppliedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            var json = DefaultJson(ctx, @"'none'");
            RunTableQuenchProc(ctx.Cmd, json);
            var first = LiveDefault(ctx.Cmd, ctx.Table, "tag");
            Assert.That(first, Is.Not.Empty, "setup: the default must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(LiveDefault(ctx.Cmd, ctx.Table, "tag"), Is.EqualTo(first),
                    $"pass {pass}: the default moved for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A function default, which PostgreSQL reframes more than a literal.
    [Test]
    public void AFunctionDefaultAuthoredInNaturalForm_IsNotReAppliedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            var json = DefaultJson(ctx, @"upper('abc')");
            RunTableQuenchProc(ctx.Cmd, json);
            var first = LiveDefault(ctx.Cmd, ctx.Table, "tag");
            Assert.That(first, Is.Not.Empty, "setup: the default must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(LiveDefault(ctx.Cmd, ctx.Table, "tag"), Is.EqualTo(first),
                    $"pass {pass}: the function default moved for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    private static string ExcludeJson(Ctx ctx, string filter) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "status", "DataType": "integer", "Nullable": true }
            ],
            "ExcludeConstraints": [
                {
                    "Name": "EX_{{ctx.Table}}_id",
                    "AccessMethod": "btree",
                    "ExcludeColumns": [ { "Column": "id", "Operator": "=" } ],
                    "FilterExpression": "{{filter}}"
                }
            ]
        }
        """;

    private static long ConstraintOid(IDbCommand cmd, string name)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT con.oid::bigint FROM pg_constraint con
                                               WHERE con.conname = '{name}'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Exclude constraints carry a predicate too, and the design lists them as a churn surface.
    [Test]
    public void AnExcludeConstraintAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = ExcludeJson(ctx, "status > 0");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = ConstraintOid(ctx.Cmd, $"EX_{ctx.Table}_id");
            Assert.That(firstOid, Is.Not.Zero, "setup: the exclude constraint must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(ConstraintOid(ctx.Cmd, $"EX_{ctx.Table}_id"), Is.EqualTo(firstOid),
                    $"pass {pass}: the exclude constraint was re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }
}
