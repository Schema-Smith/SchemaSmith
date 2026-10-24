// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Regression coverage for the TIME(n)/DATETIMEOFFSET(n) precision-loss bug: GenerateTableJson and
// ModifiedTableQuench built a column's DataType through a closed CASE that covered DATETIME2 among the
// fractional-seconds-precision types but not its two siblings, so TIME(3) and DATETIMEOFFSET(3) both
// extracted/compared as the bare type name — the live column's precision was silently dropped on every
// comparison. A declared TIME(3)/DATETIMEOFFSET(3) column therefore re-altered on EVERY deploy (asserted
// here); the sibling harm — an extract -> deploy round trip widening the column to the default precision
// of 7 — is covered by GenerateTableJsonTests' extraction assertions. The fix derives the parenthesized
// argument from INFORMATION_SCHEMA.COLUMNS.DATETIME_PRECISION via SchemaSmith.fn_ColumnTypeArguments,
// shared by extraction and drift comparison so the two sites can't render the type differently.
//
// The reliable no-rebuild signal is the procedure's own log: a real column modification emits a
// "  Altering Column <schema>.<table>.<column>" RAISERROR (captured via SqlConnection.InfoMessage).
// A converged table must emit none.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_TimePrecisionIdempotencyTests : BaseTableQuenchTests
{
    [TestCase("TIME(3)", TestName = "TableQuench_TimeWithPrecisionIsIdempotent")]
    [TestCase("DATETIMEOFFSET(3)", TestName = "TableQuench_DateTimeOffsetWithPrecisionIsIdempotent")]
    public void TableQuench_FractionalSecondsPrecisionIsIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"TimePrecisionIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $@"
DROP TABLE IF EXISTS [dbo].[{tableName}];
CREATE TABLE [dbo].[{tableName}] (
    [Id]    INT NOT NULL,
    [Stamp] {declaredType} NOT NULL,
    CONSTRAINT [{pkName}] PRIMARY KEY CLUSTERED ([Id])
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        { "Name": "[Id]",    "DataType": "INT",            "Nullable": false },
        { "Name": "[Stamp]", "DataType": "{{declaredType}}", "Nullable": false }
    ],
    "Indexes": [
        { "Name": "[{{pkName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
    ]
}
""";

        try
        {
            // Converge once (with the bug present this first run may still alter the column).
            RunTableQuenchProc(cmd, json);

            var typeBefore = GetColumnDataType(cmd, tableName, "Stamp");

            // No-op re-quench: capture only the messages from THIS pass.
            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var alterMessages = messages.FindAll(m => m.Contains("Altering Column") && m.Contains(tableName));
            Assert.That(alterMessages, Is.Empty,
                $"Re-quench of '{declaredType}' must NOT re-alter the column. " +
                "An 'Altering Column' message means the type comparison produced a phantom modification. " +
                $"Messages: {string.Join(" | ", messages)}");

            var typeAfter = GetColumnDataType(cmd, tableName, "Stamp");
            Assert.That(typeAfter, Is.EqualTo(typeBefore),
                "The stored column type must be unchanged across the no-op re-quench.");
            Assert.That(typeAfter, Is.EqualTo(declaredType),
                $"The live column must retain its declared precision — a widened default (7) would mean the round trip lost it.");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // Related bug surfaced while fixing the above: ParseTableJsonIntoTempTables.sql already
    // canonicalizes a bare-declared TIME/DATETIME2/DATETIMEOFFSET to "(7)" on the JSON side (so it
    // would match what ModifiedTableQuench builds for DATETIME2), but nothing canonicalized the
    // OTHER side — before this fix, the live-column comparison rendered a bare TIME/DATETIMEOFFSET
    // column as just the bare type name, so a bare-declared column of this family was ALSO
    // non-idempotent (declared "TIME(7)" post-canonicalization never matched extracted "TIME").
    // fn_ColumnTypeArguments always emitting precision for this family fixes both sides at once.
    [TestCase("TIME", TestName = "TableQuench_BareTimeIsIdempotent")]
    [TestCase("DATETIMEOFFSET", TestName = "TableQuench_BareDateTimeOffsetIsIdempotent")]
    public void TableQuench_BareFractionalSecondsPrecisionIsIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"BareTimePrecisionIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $@"
DROP TABLE IF EXISTS [dbo].[{tableName}];
CREATE TABLE [dbo].[{tableName}] (
    [Id]    INT NOT NULL,
    [Stamp] {declaredType} NOT NULL,
    CONSTRAINT [{pkName}] PRIMARY KEY CLUSTERED ([Id])
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        { "Name": "[Id]",    "DataType": "INT",          "Nullable": false },
        { "Name": "[Stamp]", "DataType": "{{declaredType}}", "Nullable": false }
    ],
    "Indexes": [
        { "Name": "[{{pkName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
    ]
}
""";

        try
        {
            RunTableQuenchProc(cmd, json);

            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var alterMessages = messages.FindAll(m => m.Contains("Altering Column") && m.Contains(tableName));
            Assert.That(alterMessages, Is.Empty,
                $"Re-quench of a bare '{declaredType}' must NOT re-alter the column. " +
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
