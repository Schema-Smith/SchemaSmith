// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Parity regression coverage for the string-literal-default false-change bug (the engine-specific
// fix lives in PostgreSQL, whose catalog stores varchar defaults as "'x'::character varying"). SQL
// Server wraps a stored default in parentheses — "(N'Standard')" — which ModifiedTableQuench already
// strips before comparing, so a string-literal default is expected to be idempotent here. These
// cases lock that in so a future change can't regress SQL Server into the same phantom-change class.
//
// The reliable no-rebuild signal is the procedure's own log: when a column is classified as
// modified, ModifiedTableQuench emits a "  Altering Column <schema>.<table>.<column>" RAISERROR
// (captured here via SqlConnection.InfoMessage). A converged table must emit none.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DefaultIdempotencyTests : BaseTableQuenchTests
{
    [TestCase("N'Standard'", TestName = "TableQuench_NVarcharDefaultIsIdempotent")]
    [TestCase("'Standard'", TestName = "TableQuench_NVarcharDefaultNoNPrefixIsIdempotent")]
    public void TableQuench_StringLiteralDefaultIsIdempotent(string declaredDefault)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"DefaultIdem_{uniqueId}";
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
    [Id]   INT NOT NULL,
    [Tier] NVARCHAR(20) NOT NULL CONSTRAINT [DF_{tableName}_Tier] DEFAULT {declaredDefault},
    CONSTRAINT [{pkName}] PRIMARY KEY CLUSTERED ([Id])
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        { "Name": "[Id]",   "DataType": "INT",          "Nullable": false },
        { "Name": "[Tier]", "DataType": "NVARCHAR(20)",  "Nullable": false, "Default": "{{declaredDefault}}" }
    ],
    "Indexes": [
        { "Name": "[{{pkName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
    ]
}
""";

        try
        {
            // Converge once (with a bug present this first run may still alter the column).
            RunTableQuenchProc(cmd, json);

            // No-op re-quench: capture only the messages from THIS pass.
            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var alterMessages = messages.FindAll(m => m.Contains("Altering Column") && m.Contains(tableName));
            Assert.That(alterMessages, Is.Empty,
                $"Re-quench of default '{declaredDefault}' must NOT re-alter the column. " +
                "An 'Altering Column' message means the default comparison produced a phantom modification. " +
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
