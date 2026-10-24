// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Regression coverage for the numeric/decimal whitespace + DECIMAL/NUMERIC synonym
// false-change bug. ModifiedTableQuench compared the existing column type against the
// authored DataType with a naive UPPER(...) compare and no whitespace normalization, so
// a hand-authored numeric/decimal column whose spelling differed from PostgreSQL's
// canonical "numeric(10, 2)" form only by whitespace (no-space "numeric(10,2)") or by the
// DECIMAL synonym was wrongly classed as "modified" on EVERY quench (and the spurious
// SET DATA TYPE drops/recreates a dependent primary key).
//
// The reliable no-rebuild signal is the procedure's own log: when a column is classified as
// modified, ModifiedTableQuench raises a "Modified columns found in <schema>.<table>(...)"
// NOTICE (captured here via NpgsqlConnection.Notice). A converged table must emit none.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_NumericTypeIdempotencyTests : BaseTableQuenchTests
{
    // declared form differs from canonical "numeric(10, 2)" only by whitespace / synonym.
    [TestCase("numeric(10,2)", TestName = "TableQuench_NumericNoSpaceIsIdempotent")]
    [TestCase("numeric(10, 2)", TestName = "TableQuench_NumericSpacedIsIdempotent")]
    [TestCase("DECIMAL(10,2)", TestName = "TableQuench_DecimalSynonymIsIdempotent")]
    [TestCase("DECIMAL(10, 2)", TestName = "TableQuench_DecimalSynonymSpacedIsIdempotent")]
    public void TableQuench_NumericTypeSpellingIsIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var tableName = $"NumericIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Build the table directly so its canonical type is PostgreSQL's "numeric(10,2)".
        // The numeric column is the PRIMARY KEY column so the historical bug also
        // drop/recreated the dependent primary key; the NOTICE assertion below is the
        // direct signal regardless of PostgreSQL's rewrite optimization.
        cmd.CommandText = $@"
DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";
CREATE TABLE ""{schema}"".""{tableName}"" (
    ""Amount"" numeric(10,2) NOT NULL,
    ""Note"" varchar(50) NULL,
    CONSTRAINT ""{pkName}"" PRIMARY KEY (""Amount"")
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Amount", "DataType": "{{declaredType}}", "Nullable": false },
        { "Name": "Note",   "DataType": "varchar(50)",      "Nullable": true }
    ],
    "Indexes": [
        { "Name": "{{pkName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Amount" }
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
                $"Re-quench of '{declaredType}' (canonically numeric(10,2)) must NOT re-alter the column. " +
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
