// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// G4: a foreign key's, check constraint's, exclusion constraint's, policy's and extended statistics' Description is its
// COMMENT ON, as a table's is: set, changed in place, left alone when omitted, removed by "", and untouched by a preview.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ComponentDescriptionTests : BaseTableQuenchTests
{
    private static string Json(string parent, string table, string description)
    {
        var d = description == null ? "" : $", \"Description\": \"{description}\"";
        return $$"""
            [
              { "Schema": "public", "Name": "{{parent}}",
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
                "Indexes": [ { "Name": "pk_{{parent}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] },
              { "Schema": "public", "Name": "{{table}}",
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false },
                             { "Name": "parent_id", "DataType": "integer", "Nullable": true },
                             { "Name": "code", "DataType": "integer", "Nullable": true } ],
                "ForeignKeys": [ { "Name": "fk_{{table}}", "Columns": "parent_id", "RelatedTableSchema": "public", "RelatedTable": "{{parent}}", "RelatedColumns": "id"{{d}} } ],
                "CheckConstraints": [ { "Name": "ck_{{table}}", "Expression": "id > 0"{{d}} } ],
                "ExcludeConstraints": [ { "Name": "ex_{{table}}", "AccessMethod": "btree", "ExcludeColumns": [ { "Column": "code", "Operator": "=" } ]{{d}} } ],
                "Policies": [ { "Name": "pol_{{table}}", "Command": "SELECT", "UsingExpression": "id > 0"{{d}} } ],
                "Statistics": [ { "Name": "st_{{table}}", "StatisticsColumns": "id,parent_id"{{d}} } ] }
            ]
            """;
    }

    private static string Comments(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT STRING_AGG(k || '=' || COALESCE(d, ''), '|' ORDER BY k) FROM (
              SELECT CASE con.contype WHEN 'f' THEN 'fk' WHEN 'c' THEN 'ck' ELSE 'ex' END AS k, OBJ_DESCRIPTION(con.oid, 'pg_constraint') AS d
                FROM pg_constraint con WHERE con.conrelid = 'public.{table}'::regclass AND con.contype IN ('f', 'c', 'x')
              UNION ALL
              SELECT 'pol', OBJ_DESCRIPTION(p.oid, 'pg_policy') FROM pg_policy p WHERE p.polrelid = 'public.{table}'::regclass
              UNION ALL
              SELECT 'st', OBJ_DESCRIPTION(s.oid, 'pg_statistic_ext') FROM pg_statistic_ext s WHERE s.stxrelid = 'public.{table}'::regclass) x
            """;
        return cmd.ExecuteScalar()?.ToString();
    }

    private static string Each(string value) => $"ck={value}|ex={value}|fk={value}|pol={value}|st={value}";

    [Test]
    public void EachComponentsDescription_IsSetConvergedLeftAloneWhenOmitted_AndRemovedByAnEmptyString()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var parent = $"cdesc_parent_{id}";
        var table = $"cdesc_{id}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            RunTableQuenchProc(cmd, Json(parent, table, "One"), productName: $"CDesc_{id}");
            Assert.That(Comments(cmd, table), Is.EqualTo(Each("One")), "set when created");

            RunTableQuenchProc(cmd, Json(parent, table, "Two's"), whatIf: true, productName: $"CDesc_{id}");
            Assert.That(Comments(cmd, table), Is.EqualTo(Each("One")), "a preview changes nothing");

            RunTableQuenchProc(cmd, Json(parent, table, "Two's"), productName: $"CDesc_{id}");
            Assert.That(Comments(cmd, table), Is.EqualTo(Each("Two's")), "changed in place");

            RunTableQuenchProc(cmd, Json(parent, table, null), productName: $"CDesc_{id}");
            Assert.That(Comments(cmd, table), Is.EqualTo(Each("Two's")), "an omitted one is left alone");

            RunTableQuenchProc(cmd, Json(parent, table, ""), productName: $"CDesc_{id}");
            Assert.That(Comments(cmd, table), Is.EqualTo(Each("")), "\"\" removes it");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS public.{table}; DROP TABLE IF EXISTS public.{parent};");
        }
    }

    [Test]
    public void IndexOnly_AStatisticsDescription_IsSet()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"cdesc_idxonly_{id}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Exec(cmd, $"CREATE TABLE public.{table} (id integer NOT NULL, a integer);");
            RunTableQuenchProc(cmd, $$"""
                [ { "Schema": "public", "Name": "{{table}}",
                    "Statistics": [ { "Name": "st_{{table}}", "StatisticsColumns": "id,a", "Description": "Joint id and a" } ] } ]
                """, indexOnly: true, productName: $"CDesc_{id}");
            cmd.CommandText = $"SELECT OBJ_DESCRIPTION(s.oid, 'pg_statistic_ext') FROM pg_statistic_ext s WHERE s.stxname = 'st_{table}'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("Joint id and a"));
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

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
