// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Regression coverage for the timestamptz(n)/time(n)/timetz(n) precision-loss bug: GenerateTableJson and
// ModifiedTableQuench built a column's DataType through a closed CASE that special-cased only 'timestamp'
// among PostgreSQL's fractional-seconds-precision types, so timestamptz(3) fell through to no argument at
// all — the live column's precision was silently dropped on every comparison. A declared timestamptz(3)
// column therefore re-altered on EVERY quench (asserted here); the sibling harm — an extract -> deploy
// round trip widening the column to the default precision of 6 — is covered by GenerateTableJsonTests'
// extraction assertions. The fix derives the parenthesized argument from
// information_schema.columns.datetime_precision via "SchemaSmith"."ColumnTypeArguments", shared by
// extraction and drift comparison so the two sites can't render the type differently.
//
// The reliable no-rebuild signal is the procedure's own log: ModifiedTableQuench raises a "Modified
// columns found in <schema>.<table>(...)" NOTICE when it classifies a column as changed (captured via
// NpgsqlConnection.Notice). A converged table must emit none.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_TimePrecisionIdempotencyTests : BaseTableQuenchTests
{
    [TestCase("timestamptz(3)", TestName = "TableQuench_TimestampTzWithPrecisionIsIdempotent")]
    [TestCase("time(3)", TestName = "TableQuench_TimeWithPrecisionIsIdempotent")]
    public void TableQuench_FractionalSecondsPrecisionIsIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var tableName = $"TimePrecisionIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $@"
DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";
CREATE TABLE ""{schema}"".""{tableName}"" (
    ""Id""    integer NOT NULL,
    ""Stamp"" {declaredType} NOT NULL,
    CONSTRAINT ""{pkName}"" PRIMARY KEY (""Id"")
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id",    "DataType": "integer",       "Nullable": false },
        { "Name": "Stamp", "DataType": "{{declaredType}}", "Nullable": false }
    ],
    "Indexes": [
        { "Name": "{{pkName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
    ]
}
""";

        try
        {
            // Converge once (with the bug present this first run may still alter the column).
            RunTableQuenchProc(cmd, json);

            // No-op re-quench: capture only the notices from THIS pass.
            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var modifiedMessages = messages.FindAll(m => m.Contains("Modified columns found") && m.Contains(tableName));
            Assert.That(modifiedMessages, Is.Empty,
                $"Re-quench of '{declaredType}' must NOT re-alter the column. " +
                "A 'Modified columns found' notice means the type comparison produced a phantom modification. " +
                $"Notices: {string.Join(" | ", messages)}");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }
}
