// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Coverage for column-level CheckExpression on PostgreSQL (#313). A column declaring a
// CheckExpression must materialize as a real, enforced check constraint named
// CK_<table>_<column>, must be idempotent across re-quench (no phantom drop/recreate), and
// must re-apply when the authored expression changes.
//
// The authored expression is written in PostgreSQL's canonical stored form ("Quantity" > 0)
// so the modify-detection — which reuses the SAME pg_get_constraintdef normalization as the
// table-level check pass — compares equal to the live definition and leaves the constraint
// untouched on a no-op run.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ColumnCheckExpressionTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ColumnCheckConstraintIsCreatedWithDeterministicName()
    {
        var ctx = NewTable();
        try
        {
            RunTableQuenchProc(ctx.Cmd, Json(ctx, "\\\"Quantity\\\" > 0"));

            ctx.Cmd.CommandText = $@"
SELECT pg_catalog.PG_GET_CONSTRAINTDEF(con.oid)
  FROM pg_catalog.pg_constraint con
  JOIN pg_catalog.pg_class rel ON rel.oid = con.conrelid
  JOIN pg_catalog.pg_namespace nsp ON nsp.oid = rel.relnamespace
 WHERE con.contype = 'c'
   AND nsp.nspname = '{ctx.Schema}'
   AND rel.relname = '{ctx.Table}'
   AND con.conname = 'CK_{ctx.Table}_Quantity';";
            var def = ctx.Cmd.ExecuteScalar()?.ToString();

            Assert.That(def, Is.Not.Null,
                $"Expected a check constraint named CK_{ctx.Table}_Quantity to exist after quench.");
            Assert.That(def, Does.Contain("Quantity"),
                "The created constraint definition must reference the Quantity column.");
        }
        finally { ctx.Drop(); }
    }

    [Test]
    public void TableQuench_ColumnCheckConstraintIsEnforced()
    {
        var ctx = NewTable();
        try
        {
            RunTableQuenchProc(ctx.Cmd, Json(ctx, "\\\"Quantity\\\" > 0"));

            ctx.Cmd.CommandText = $@"INSERT INTO ""{ctx.Schema}"".""{ctx.Table}"" (""Quantity"") VALUES (-5);";
            Assert.That(() => ctx.Cmd.ExecuteNonQuery(),
                Throws.Exception.With.Message.Contains("CK_" + ctx.Table + "_Quantity"),
                "A row violating the column CheckExpression must be rejected by the constraint.");
        }
        finally { ctx.Drop(); }
    }

    [Test]
    public void TableQuench_ColumnCheckConstraintIsIdempotent()
    {
        var ctx = NewTable();
        try
        {
            // Converge once.
            RunTableQuenchProc(ctx.Cmd, Json(ctx, "\\\"Quantity\\\" > 0"));

            // No-op re-quench: capture only the notices from THIS pass.
            ctx.Messages.Clear();
            RunTableQuenchProc(ctx.Cmd, Json(ctx, "\\\"Quantity\\\" > 0"));

            var checkNotices = ctx.Messages.FindAll(m =>
                (m.Contains("Add missing column check constraint") || m.Contains("Column Check Constraint"))
                && m.Contains(ctx.Table));
            Assert.That(checkNotices, Is.Empty,
                "Re-quench of an unchanged column CheckExpression must NOT drop/recreate the constraint. " +
                $"Notices: {string.Join(" | ", ctx.Messages)}");
        }
        finally { ctx.Drop(); }
    }

    [Test]
    public void TableQuench_ColumnCheckConstraintIsReappliedWhenExpressionChanges()
    {
        var ctx = NewTable();
        try
        {
            RunTableQuenchProc(ctx.Cmd, Json(ctx, "\\\"Quantity\\\" > 0"));

            // A value satisfying the OLD expression (Quantity > 0) but violating the NEW one (Quantity > 100).
            ctx.Cmd.CommandText = $@"INSERT INTO ""{ctx.Schema}"".""{ctx.Table}"" (""Quantity"") VALUES (50);";
            ctx.Cmd.ExecuteNonQuery();
            ctx.Cmd.CommandText = $@"DELETE FROM ""{ctx.Schema}"".""{ctx.Table}"";";
            ctx.Cmd.ExecuteNonQuery();

            // Re-quench with a semantically distinct expression.
            RunTableQuenchProc(ctx.Cmd, Json(ctx, "\\\"Quantity\\\" > 100"));

            ctx.Cmd.CommandText = $@"INSERT INTO ""{ctx.Schema}"".""{ctx.Table}"" (""Quantity"") VALUES (50);";
            Assert.That(() => ctx.Cmd.ExecuteNonQuery(),
                Throws.Exception.With.Message.Contains("CK_" + ctx.Table + "_Quantity"),
                "After changing the column CheckExpression to Quantity > 100, a row with Quantity = 50 must be rejected.");
        }
        finally { ctx.Drop(); }
    }

    private sealed class TableContext : IDisposable
    {
        public NpgsqlConnection Conn = null!;
        public System.Data.IDbCommand Cmd = null!;
        public string Schema = null!;
        public string Table = null!;
        public List<string> Messages = null!;

        public void Drop()
        {
            Cmd.CommandText = $@"DROP TABLE IF EXISTS ""{Schema}"".""{Table}"";";
            Cmd.ExecuteNonQuery();
            Conn.Close();
        }

        public void Dispose() => Conn.Dispose();
    }

    private TableContext NewTable()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var table = $"ColCheck_{uniqueId}";

        var messages = new List<string>();
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $@"
DROP TABLE IF EXISTS ""{schema}"".""{table}"";
CREATE TABLE ""{schema}"".""{table}"" (""Quantity"" integer NULL);";
        cmd.ExecuteNonQuery();

        return new TableContext { Conn = conn, Cmd = cmd, Schema = schema, Table = table, Messages = messages };
    }

    // The authored CheckExpression is in PostgreSQL canonical form so a no-op re-quench compares equal.
    private static string Json(TableContext ctx, string checkExpression) => $$"""
{
    "Schema": "{{ctx.Schema}}",
    "Name": "{{ctx.Table}}",
    "Columns": [
        { "Name": "Quantity", "DataType": "integer", "Nullable": true, "CheckExpression": "{{checkExpression}}" }
    ]
}
""";
}
