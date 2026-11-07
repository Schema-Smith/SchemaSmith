// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// #610: a key that is a function call with more than one argument was split on its comma and rebuilt on every deploy,
// however it was written; so was a parenthesised key, which the engine renders without the parentheses.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ExpressionKeyChurnTests : BaseTableQuenchTests
{
    [TestCase("to_tsvector('english', notes)", "gin")]
    [TestCase("to_tsvector('english'::regconfig, notes)", "gin")]
    [TestCase("(lower(notes))", "btree")]
    [TestCase("lower(notes), id", "btree")]
    [TestCase("COALESCE(notes, 'x'), id", "btree")]
    public void AnExpressionKey_IsNotRebuilt_OnTheSecondDeploy(string keys, string method)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"exprkey_{id}";
        var json = $$"""
            [{ "Schema": "public", "Name": "{{table}}",
               "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "notes", "DataType": "text", "Nullable": true } ],
               "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                            { "Name": "ix_{{table}}", "AccessMethod": "{{method}}", "IndexColumns": "{{keys}}" } ] }]
            """;
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            RunTableQuenchProc(cmd, json, productName: $"ExprKey_{id}");
            Exec(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()");
            RunTableQuenchProc(cmd, json, productName: $"ExprKey_{id}");
            cmd.CommandText = $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" LIKE '%ix_{table}%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "an unchanged expression key is left alone");
        }
        finally
        {
            Exec(cmd, $"DROP TABLE IF EXISTS public.{table};");
        }
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
