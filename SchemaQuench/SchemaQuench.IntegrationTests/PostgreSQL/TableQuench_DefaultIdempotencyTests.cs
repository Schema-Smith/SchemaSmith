// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Regression coverage for the string-literal-default false-change bug. PostgreSQL stores a
// VARCHAR/CHAR column default as "'value'::character varying" (an explicit type cast), but
// ModifiedTableQuench compared the catalog default against the authored Default with a plain
// string compare and no cast normalization. A hand-authored package declares "'Standard'" while
// the catalog reports "'Standard'::character varying", so the column was wrongly classed as
// "modified" on EVERY quench (re-issuing ALTER COLUMN ... SET DEFAULT). The mirror case also
// matters: SchemaTongs extraction writes the cast form back into the package, so an extracted
// (round-tripped) package declares "'Standard'::character varying" — both forms must converge to
// a no-op against the same catalog value, which requires normalizing BOTH sides of the compare.
//
// The reliable no-rebuild signal is the procedure's own log: when a column is classified as
// modified, ModifiedTableQuench raises a "Modified columns found in <schema>.<table>(...)"
// NOTICE (captured here via NpgsqlConnection.Notice). A converged table must emit none.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DefaultIdempotencyTests : BaseTableQuenchTests
{
    // The table's varchar default is stored canonically as "'Standard'::character varying".
    // Each declared form must compare equal to that stored form after cast normalization:
    //  - the bare hand-authored literal, and
    //  - the cast form SchemaTongs writes back on extraction (round-trip).
    [TestCase("'Standard'", TestName = "TableQuench_StringDefaultBareLiteralIsIdempotent")]
    [TestCase("'Standard'::character varying", TestName = "TableQuench_StringDefaultExtractedCastFormIsIdempotent")]
    public void TableQuench_StringLiteralDefaultIsIdempotent(string declaredDefault)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var tableName = $"DefaultIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Build the table directly so PostgreSQL stores the default canonically as
        // "'Standard'::character varying".
        cmd.CommandText = $@"
DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";
CREATE TABLE ""{schema}"".""{tableName}"" (
    ""Id""   integer NOT NULL,
    ""Tier"" varchar(20) NOT NULL DEFAULT 'Standard',
    CONSTRAINT ""{pkName}"" PRIMARY KEY (""Id"")
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id",   "DataType": "integer",     "Nullable": false },
        { "Name": "Tier", "DataType": "varchar(20)", "Nullable": false, "Default": "{{declaredDefault}}" }
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
                $"Re-quench of default '{declaredDefault}' (stored as 'Standard'::character varying) must NOT re-alter the column. " +
                "A 'Modified columns found' notice means the default comparison produced a phantom modification. " +
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
