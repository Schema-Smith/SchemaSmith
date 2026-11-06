// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// B7: an index removed from the package was taken for a constraint whenever any object in its schema had its name, so
// a view named like a plain index turned its removal into ALTER TABLE ... DROP CONSTRAINT, which fails.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_RemovedIndexNameCollisionTests : BaseTableQuenchTests
{
    private static string Json(string table, bool withIndex) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",   "DataType": "INT",         "Nullable": false },
      { "Name": "[Code]", "DataType": "VARCHAR(20)", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
      {{(withIndex ? $", {{ \"Name\": \"[IX_{table}]\", \"IndexColumns\": \"[Code]\" }}" : "")}}
    ]
  }
]
""";

    [Test]
    public void ARemovedIndexNamedLikeAView_IsDroppedAsAnIndex()
    {
        var table = $"IdxColl_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, withIndex: true));
            cmd.CommandText = $"EXEC('CREATE VIEW [dbo].[IX_{table}] AS SELECT 1 AS x')";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, withIndex: false)));
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'IX_{table}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "the removed index is dropped");
        }
        finally
        {
            cmd.CommandText = $"DROP VIEW IF EXISTS [dbo].[IX_{table}]; DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }
}
