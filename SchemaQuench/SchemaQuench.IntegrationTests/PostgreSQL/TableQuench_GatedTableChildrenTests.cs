// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// #612: a table whose ShouldApplyExpression is false was skipped, but its keys, checks, foreign keys and
// statistics were parsed from the JSON on their own and still applied, so the deploy failed on a table that
// was never created. They go with their table, and a same-named variant keeps only its own.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_GatedTableChildrenTests : BaseTableQuenchTests
{
    [Test]
    public void AGatedOffTable_TakesItsKeysChecksForeignKeysAndStatisticsWithIt()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var parent = $"gate_parent_{id}";
        var table = $"gate_child_{id}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            var json = $$"""
            [
              { "Schema": "public", "Name": "{{parent}}",
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
                "Indexes": [ { "Name": "pk_{{parent}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] },
              { "Schema": "public", "Name": "{{table}}", "ShouldApplyExpression": "1 = 0",
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false },
                             { "Name": "parent_id", "DataType": "integer", "Nullable": true } ],
                "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                             { "Name": "ix_{{table}}_parent", "IndexColumns": "parent_id" } ],
                "CheckConstraints": [ { "Name": "ck_{{table}}", "Expression": "id > 0" } ],
                "ForeignKeys": [ { "Name": "fk_{{table}}", "Columns": "parent_id", "RelatedTableSchema": "public", "RelatedTable": "{{parent}}", "RelatedColumns": "id" } ],
                "Statistics": [ { "Name": "st_{{table}}", "Columns": "id, parent_id" } ] }
            ]
            """;
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: $"Gate_{id}"));
            Assert.Multiple(() =>
            {
                Assert.That(Exists(cmd, $"SELECT to_regclass('public.{parent}') IS NOT NULL"), Is.True, "the ungated table is deployed");
                Assert.That(Exists(cmd, $"SELECT to_regclass('public.{table}') IS NOT NULL"), Is.False, "the gated table is skipped");
                Assert.That(Exists(cmd, $"SELECT EXISTS (SELECT 1 FROM pg_statistic_ext WHERE stxname = 'st_{table}')"), Is.False, "its statistics went with it");
            });
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS public.{table}; DROP TABLE IF EXISTS public.{parent};");
        }
    }

    [Test]
    public void OfTwoSameNamedVariants_OnlyTheAppliedOnesIndexesDeploy()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"gate_variant_{id}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            var json = $$"""
            [
              { "Schema": "public", "Name": "{{table}}", "VariantName": "off", "ShouldApplyExpression": "1 = 0",
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "integer", "Nullable": true } ],
                "Indexes": [ { "Name": "ix_{{table}}_a", "IndexColumns": "a" } ] },
              { "Schema": "public", "Name": "{{table}}", "VariantName": "on", "ShouldApplyExpression": "1 = 1",
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "b", "DataType": "integer", "Nullable": true } ],
                "Indexes": [ { "Name": "ix_{{table}}_b", "IndexColumns": "b" } ] }
            ]
            """;
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: $"Gate_{id}"));
            Assert.Multiple(() =>
            {
                Assert.That(Exists(cmd, $"SELECT to_regclass('public.ix_{table}_b') IS NOT NULL"), Is.True, "the applied variant's index");
                Assert.That(Exists(cmd, $"SELECT to_regclass('public.ix_{table}_a') IS NOT NULL"), Is.False, "the gated variant's index stays out");
            });
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS public.{table};");
        }
    }

    [Test]
    public void IndexOnly_AGatedOffTablesIndexes_AreNotApplied()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"gate_idxonly_{id}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Exec(cmd, $"CREATE TABLE public.{table} (id integer NOT NULL, a integer, b integer);");
            var json = $$"""
            [
              { "Schema": "public", "Name": "{{table}}", "ShouldApplyExpression": "1 = 0",
                "Indexes": [ { "Name": "ix_{{table}}_a", "IndexColumns": "a" } ],
                "Statistics": [ { "Name": "st_{{table}}", "Columns": "a, b" } ] },
              { "Schema": "public", "Name": "{{table}}", "ShouldApplyExpression": "1 = 1",
                "Indexes": [ { "Name": "ix_{{table}}_b", "IndexColumns": "b" } ] }
            ]
            """;
            RunTableQuenchProc(cmd, json, indexOnly: true, productName: $"Gate_{id}");
            Assert.Multiple(() =>
            {
                Assert.That(Exists(cmd, $"SELECT to_regclass('public.ix_{table}_b') IS NOT NULL"), Is.True, "the applied entry's index");
                Assert.That(Exists(cmd, $"SELECT to_regclass('public.ix_{table}_a') IS NOT NULL"), Is.False, "the gated entry's index");
                Assert.That(Exists(cmd, $"SELECT EXISTS (SELECT 1 FROM pg_statistic_ext WHERE stxname = 'st_{table}')"), Is.False, "the gated entry's statistics");
            });
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS public.{table};");
        }
    }

    private NpgsqlConnection Open()
    {
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static bool Exists(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return (bool)cmd.ExecuteScalar()!;
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
