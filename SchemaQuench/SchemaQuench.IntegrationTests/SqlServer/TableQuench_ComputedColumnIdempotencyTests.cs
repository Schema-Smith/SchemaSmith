// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Pinning regression coverage for a computed-column idempotency bug:
// JSON declarations of "Persisted": true, "Nullable": false on a computed
// column (the shape used in the shipped Northwind demo's
// recyclebin.Registry.ExpirationDate) round-trip into is_nullable=1 on the
// live table because the DDL-emission paths build "AS (expr) PERSISTED"
// without appending " NOT NULL" — even though SQL Server fully supports
// "AS (expr) PERSISTED NOT NULL" on deterministic computed columns. The
// JSON-vs-live nullability mismatch then trips drift detection on every
// re-quench, emitting a destructive DROP COLUMN with no successful re-add
// (the re-add hits the same gap so the cycle never converges). When the
// column is referenced by an index, the cascade also drops that index.
//
// Affected DDL-emission sites (all platform Schema scripts):
//   * ParseTableJsonIntoTempTables.sql ColumnScript               (line 60)
//   * ModifiedTableQuench.sql #ColumnChanges.ColumnScript         (line 118)
//   * ModifiedTableQuench.sql Add Missing Computed Columns        (line 194)
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ComputedColumnIdempotencyTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldHonorNotNullOnPersistedComputedColumn()
    {
        // The computed column should be created with is_nullable=0 when JSON
        // declares Persisted:true + Nullable:false. Without the fix, the
        // emitted DDL is "AS (expr) PERSISTED" — no NOT NULL — and SQL Server
        // defaults computed columns to is_nullable=1.
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NotNullPersistedComputed_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // No pre-existing table — let TableQuench create it from JSON. Mirrors
        // the demo Northwind recyclebin.Registry shape: NOT NULL anchor, NOT
        // NULL retention days, NOT NULL persisted computed column.
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        { "Name": "[Id]",            "DataType": "INT IDENTITY(1, 1)", "Nullable": false },
        { "Name": "[RecycledDate]",  "DataType": "DATETIME2",          "Nullable": false },
        { "Name": "[RetentionDays]", "DataType": "INT",                "Nullable": false, "Default": "90" },
        {
            "Name": "[ExpirationDate]",
            "DataType": "DATETIME2",
            "Nullable": false,
            "ComputedExpression": "DATEADD(DAY, [RetentionDays], [RecycledDate])",
            "Persisted": true
        }
    ],
    "Indexes": [
        { "Name": "[PK_{{tableName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Sanity: the table and computed column exist.
        cmd.CommandText = $"SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('[dbo].[{tableName}]'), 'ExpirationDate', 'IsComputed') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "ExpirationDate should be a computed column");

        cmd.CommandText = $"SELECT [is_persisted] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('[dbo].[{tableName}]') AND [name] = 'ExpirationDate'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "ExpirationDate should be persisted");

        // The bug: live is_nullable should be 0 because JSON declared Nullable:false,
        // but without the fix it's 1 because the emitted DDL omitted NOT NULL.
        cmd.CommandText = $"SELECT [is_nullable] FROM sys.columns WHERE [object_id] = OBJECT_ID('[dbo].[{tableName}]') AND [name] = 'ExpirationDate'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "ExpirationDate should be NOT NULL — JSON declared Nullable:false on a Persisted:true computed column, which SQL Server supports as 'AS (expr) PERSISTED NOT NULL'");

        // Cleanup
        cmd.CommandText = $"DROP TABLE [dbo].[{tableName}]";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldEndUpWithCorrectStateAfterRequench()
    {
        // End-state correctness check on a NOT NULL persisted computed column
        // shape (mirrors the shipped Northwind demo's recyclebin.Registry.
        // ExpirationDate). Idempotency at the column_id level is NOT asserted —
        // the comparison-side fix that would have made it idempotent
        // (fn_NormalizeExpression) was reverted as unsound: any T-SQL string
        // normalizer that handles whitespace+case agnostically without a real
        // SQL parser introduces false-negative classes (silent-skip on
        // string-literal whitespace, etc.) that are more dangerous than the
        // false-positive drop+re-add cycle this test would otherwise catch.
        // The .NET-side parser-aware comparison is the proper architectural
        // fix; tracked separately. Until then: end-state must remain correct
        // across re-quench, but a destructive drop+re-add (column_id bumps)
        // is accepted.
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NoDriftPersistedComputed_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        { "Name": "[Id]",            "DataType": "INT IDENTITY(1, 1)", "Nullable": false },
        { "Name": "[RecycledDate]",  "DataType": "DATETIME2",          "Nullable": false },
        { "Name": "[RetentionDays]", "DataType": "INT",                "Nullable": false, "Default": "90" },
        {
            "Name": "[ExpirationDate]",
            "DataType": "DATETIME2",
            "Nullable": false,
            "ComputedExpression": "DATEADD(DAY, [RetentionDays], [RecycledDate])",
            "Persisted": true
        }
    ],
    "Indexes": [
        { "Name": "[PK_{{tableName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
        { "Name": "[IX_{{tableName}}_Exp]", "PrimaryKey": false, "Unique": false, "Clustered": false, "IndexColumns": "[ExpirationDate]" }
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Capture live definition after first quench to check end-state stability.
        cmd.CommandText = $"SELECT [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('[dbo].[{tableName}]') AND [name] = 'ExpirationDate'";
        var liveDefinitionBefore = cmd.ExecuteScalar()?.ToString();

        cmd.CommandText = $"SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('[dbo].[{tableName}]'), 'IX_{tableName}_Exp', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "Index on the computed column should exist after first quench");

        // Re-quench the same JSON. With the comparison-side fix reverted, this
        // currently re-runs the drop+re-add cycle (false positive on whitespace
        // difference between authored JSON and SQL Server's stored canonical
        // form). The end state must still be correct.
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Computed column still exists, still computed, still persisted.
        cmd.CommandText = $"SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('[dbo].[{tableName}]'), 'ExpirationDate', 'IsComputed') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "ExpirationDate should still be a computed column after re-quench");

        cmd.CommandText = $"SELECT [is_persisted] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('[dbo].[{tableName}]') AND [name] = 'ExpirationDate'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "ExpirationDate should still be persisted after re-quench");

        cmd.CommandText = $"SELECT [is_nullable] FROM sys.columns WHERE [object_id] = OBJECT_ID('[dbo].[{tableName}]') AND [name] = 'ExpirationDate'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "ExpirationDate should still be NOT NULL after re-quench (Bug A coverage)");

        cmd.CommandText = $"SELECT [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('[dbo].[{tableName}]') AND [name] = 'ExpirationDate'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(liveDefinitionBefore), "ExpirationDate definition should be unchanged after re-quench — even if the column went through a drop+re-add cycle, the re-add must produce the same definition");

        // Dependent index must end up present after re-quench, even if the
        // false-positive drift dropped it as a cascade. SchemaQuench's
        // MissingIndexesAndConstraints pass restores it.
        cmd.CommandText = $"SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('[dbo].[{tableName}]'), 'IX_{tableName}_Exp', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "Dependent index on ExpirationDate should still exist after re-quench (recreated if cascade-dropped)");

        // Cleanup
        cmd.CommandText = $"DROP TABLE [dbo].[{tableName}]";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Through the model, as the deploy sends it. An omitted Nullable on a PERSISTED computed column is the engine's
    // to decide. The product used to serialize every omitted Nullable as an explicit false, so this reached the
    // procedure as a NOT NULL request: against a live nullable column whose rows make the expression NULL, the
    // deploy dropped the column, failed to put it back, and left the table without it.
    [Test]
    public void AnOmittedNullable_LeavesAnExistingNullablePersistedComputedColumn_InPlace()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"OmittedNullPersisted_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = $@"SET QUOTED_IDENTIFIER ON;
                CREATE TABLE dbo.[{tableName}] ([Id] INT NOT NULL, [RetentionDays] INT NULL, [RetentionWeeks] AS ([RetentionDays] / 7) PERSISTED);
                INSERT dbo.[{tableName}] ([Id], [RetentionDays]) VALUES (1, NULL), (2, 30);";
            cmd.ExecuteNonQuery();
            var columnId = ColumnId(cmd, tableName, "RetentionWeeks");

            var json = DeployJson.ThroughTheModel($$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[{{tableName}}]",
                    "Columns": [
                        { "Name": "[Id]", "DataType": "INT" },
                        { "Name": "[RetentionDays]", "DataType": "INT", "Nullable": true },
                        { "Name": "[RetentionWeeks]", "DataType": "INT", "ComputedExpression": "[RetentionDays]/(7)", "Persisted": true }
                    ]
                }
                """, Platform.SqlServer);
            // Authored in SQL Server's stored form: this table has no ExpressionMap record yet, so a differently
            // spelled expression would be re-applied once by design, and nullability must be the only question.
            Assert.That(json, Does.Not.Contain("\"Nullable\": false"),
                "precondition: an omitted Nullable must reach the procedure as omitted");

            RunTableQuenchProc(cmd, json);

            Assert.That(ColumnId(cmd, tableName, "RetentionWeeks"), Is.EqualTo(columnId),
                "the existing nullable computed column was dropped although the package never asked for NOT NULL");
            cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{tableName}] WHERE [RetentionWeeks] IS NULL";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the row whose expression is NULL must survive");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{tableName}]";
            cmd.ExecuteNonQuery();
        }
    }

    [TestCase(null, true)]
    [TestCase(false, false)]
    public void APersistedComputedColumn_IsCreatedWithTheNullabilityThePackageAsksFor(bool? declared, bool expectNullable)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"CreatedNullPersisted_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            var nullable = declared.HasValue ? $", \"Nullable\": {declared.Value.ToString().ToLowerInvariant()}" : "";
            var json = DeployJson.ThroughTheModel($$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[{{tableName}}]",
                    "Columns": [
                        { "Name": "[Id]", "DataType": "INT" },
                        { "Name": "[RetentionDays]", "DataType": "INT" },
                        { "Name": "[RetentionWeeks]", "DataType": "INT", "ComputedExpression": "RetentionDays / 7", "Persisted": true{{nullable}} }
                    ]
                }
                """, Platform.SqlServer);

            RunTableQuenchProc(cmd, json);
            var columnId = ColumnId(cmd, tableName, "RetentionWeeks");
            cmd.CommandText = $"SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{tableName}]') AND name = 'RetentionWeeks'";
            Assert.That(Convert.ToBoolean(cmd.ExecuteScalar()), Is.EqualTo(expectNullable));

            RunTableQuenchProc(cmd, json);
            Assert.That(ColumnId(cmd, tableName, "RetentionWeeks"), Is.EqualTo(columnId), "the rerun must leave the column alone");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{tableName}]";
            cmd.ExecuteNonQuery();
        }
    }

    private static int ColumnId(System.Data.IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $"SELECT ISNULL((SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = '{column}'), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
