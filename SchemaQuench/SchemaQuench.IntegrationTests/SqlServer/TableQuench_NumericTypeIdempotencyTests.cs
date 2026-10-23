// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Regression coverage for the numeric/decimal whitespace + DECIMAL/NUMERIC synonym
// false-change bug. ModifiedTableQuench's #ColumnChanges type compare normalized ONLY the
// exact ", " -> "," case (REPLACE(<built>, ', ', ',') <> REPLACE(c.DataType, ', ', ',')),
// so it was intolerant of other spacing (e.g. a space before/inside the parens) and of the
// DECIMAL/NUMERIC synonym. A hand-authored numeric/decimal column whose spelling differed
// from SQL Server's canonical "NUMERIC(10, 2)" form only by whitespace or the DECIMAL
// synonym was wrongly classed as "modified" on EVERY quench, re-issuing ALTER COLUMN.
//
// The reliable no-rebuild signal is the procedure's own log: when a column is classified as
// modified, ModifiedTableQuench emits a "  Altering Column <schema>.<table>.<column>"
// RAISERROR (captured here via SqlConnection.InfoMessage). A converged table must emit none.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_NumericTypeIdempotencyTests : BaseTableQuenchTests
{
    // The table is created as NUMERIC(10, 2), which SQL Server stores canonically as
    // "decimal(10, 2)". Each declared form differs from that canonical form only by extra
    // whitespace and/or the NUMERIC synonym. (Plain "DECIMAL(10,2)" / "DECIMAL(10, 2)" are
    // already tolerated by the prior partial ", "->"," normalization, so the regressing
    // forms here are the synonym + odd spacing.)
    [TestCase("NUMERIC(10,2)", TestName = "TableQuench_NumericSynonymIsIdempotent")]
    [TestCase("NUMERIC(10, 2)", TestName = "TableQuench_NumericSynonymSpacedIsIdempotent")]
    [TestCase("DECIMAL( 10, 2)", TestName = "TableQuench_DecimalSpaceAfterParenIsIdempotent")]
    [TestCase("DECIMAL(10, 2 )", TestName = "TableQuench_DecimalSpaceBeforeCloseParenIsIdempotent")]
    public void TableQuench_NumericTypeSpellingIsIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NumericIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Build the table directly so its canonical type is SQL Server's "NUMERIC(10, 2)".
        cmd.CommandText = $@"
DROP TABLE IF EXISTS [dbo].[{tableName}];
CREATE TABLE [dbo].[{tableName}] (
    [Amount] NUMERIC(10, 2) NOT NULL,
    [Note] VARCHAR(50) NULL,
    CONSTRAINT [{pkName}] PRIMARY KEY CLUSTERED ([Amount])
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        { "Name": "[Amount]", "DataType": "{{declaredType}}", "Nullable": false },
        { "Name": "[Note]",   "DataType": "VARCHAR(50)",      "Nullable": true }
    ],
    "Indexes": [
        { "Name": "[{{pkName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Amount]" }
    ]
}
""";

        try
        {
            // Converge once (with the bug present this first run may still alter the column).
            RunTableQuenchProc(cmd, json);

            // No-op re-quench: capture only the messages from THIS pass.
            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var alterMessages = messages.FindAll(m => m.Contains("Altering Column") && m.Contains(tableName));
            Assert.That(alterMessages, Is.Empty,
                $"Re-quench of '{declaredType}' (canonically NUMERIC(10, 2)) must NOT re-alter the column. " +
                "An 'Altering Column' message means the type comparison produced a phantom modification. " +
                $"Messages: {string.Join(" | ", messages)}");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }
}
