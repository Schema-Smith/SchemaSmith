// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// #613: an owned table replaced by a same-named view kept its ownership row, so drop-by-absence tried to drop the
// view as a table (42809) and every deploy failed from then on.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_TableReplacedByViewTests : BaseTableQuenchTests
{
    [Test]
    public void AnOwnedTableReplacedByAView_IsNotDroppedAsATable_AndItsOwnershipGoes()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var product = $"ViewSplit_{id}";
        var table = $"viewsplit_customer_{id}";
        var keep = $"viewsplit_keep_{id}";
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            RunTableQuenchProc(cmd, Tables(table, keep), productName: product);
            Exec(cmd, $"DROP TABLE public.{table}; CREATE VIEW public.{table} AS SELECT id FROM public.{keep};");

            Exec(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()");
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Tables(keep), dropTablesRemovedFromProduct: true, whatIf: true, productName: product), "WhatIf");
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectType\" = 'table' AND \"ObjectName\" = 'public.{table}'"),
                Is.Zero, "a preview does not predict dropping the view");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Tables(keep), dropTablesRemovedFromProduct: true, productName: product), "first deploy");
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Tables(keep), dropTablesRemovedFromProduct: true, productName: product), "and every one after");
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM pg_views WHERE schemaname = 'public' AND viewname = '{table}'"), Is.EqualTo(1), "the view is left alone");
        }
        finally
        {
            Exec(cmd, $"DROP VIEW IF EXISTS public.{table}; DROP TABLE IF EXISTS public.{table}; DROP TABLE IF EXISTS public.{keep};");
            Exec(cmd, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }

    private static string Tables(params string[] names) =>
        "[" + string.Join(",", Array.ConvertAll(names, n => $$"""
          { "Schema": "public", "Name": "{{n}}",
            "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
            "Indexes": [ { "Name": "pk_{{n}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
          """)) + "]";

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
