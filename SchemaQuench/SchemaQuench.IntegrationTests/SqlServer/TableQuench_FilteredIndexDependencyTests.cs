// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// A7: whether a filtered index, filtered statistic or table-level check reads a changing column was a bare substring
// test, so a change to [Status] dropped and re-created an index filtered on [OrderStatus], and a column named [Rate] was
// a LIKE character class that matched nothing, leaving its filtered index in place for the ALTER to fail on (5074).
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_FilteredIndexDependencyTests : BaseTableQuenchTests
{
    private static string Json(string table, string statusType, string rateType) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",          "DataType": "INT",            "Nullable": false },
      { "Name": "[Status]",      "DataType": "{{statusType}}", "Nullable": true },
      { "Name": "[OrderStatus]", "DataType": "VARCHAR(10)",    "Nullable": true },
      { "Name": "[Rate]",        "DataType": "{{rateType}}",   "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
      { "Name": "[IX_{{table}}_Open]", "IndexColumns": "[Id]", "FilterExpression": "[OrderStatus] = 'open'" },
      { "Name": "[IX_{{table}}_Rate]", "IndexColumns": "[Id]", "FilterExpression": "[Rate] > 0" }
    ]
  }
]
""";

    [Test]
    public void AChangedColumn_TouchesOnlyTheFilteredIndexesThatReadIt()
    {
        var table = $"FiltDep_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Partition(string index)
        {
            cmd.CommandText = $"SELECT CAST(p.partition_id AS VARCHAR(30)) FROM sys.indexes i JOIN sys.partitions p ON p.[object_id] = i.[object_id] AND p.index_id = i.index_id WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.[name] = '{index}'";
            return cmd.ExecuteScalar()?.ToString();
        }
        try
        {
            RunTableQuenchProc(cmd, Json(table, "VARCHAR(10)", "INT"));
            var open = Partition($"IX_{table}_Open");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, "VARCHAR(20)", "BIGINT")),
                "the index filtered on [Rate] comes off before [Rate] changes type");
            Assert.That(Partition($"IX_{table}_Open"), Is.EqualTo(open), "a change to [Status] leaves the index filtered on [OrderStatus]");
            Assert.That(Partition($"IX_{table}_Rate"), Is.Not.Null, "the index filtered on [Rate] is back");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }
}
