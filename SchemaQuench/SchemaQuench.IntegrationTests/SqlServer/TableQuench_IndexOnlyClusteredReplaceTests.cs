// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Repro for #302: replacing a table's clustered index in index-only mode failed with SQL Server
/// error 1913 ("Cannot create more than one clustered index"). The full table quench drops a
/// conflicting clustered index before creating the new one, but the index-only path did not — it
/// only created the new clustered index, while a different clustered index still occupied the slot.
/// Exercised with DropUnknownIndexes OFF (the replication-overlay case: don't drop the base table's
/// other indexes, but the clustered slot can still hold only one).
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_IndexOnlyClusteredReplaceTests : BaseTableQuenchTests
{
    [Test]
    public void IndexOnly_ReplacesClusteredIndex_WhenAnotherClusteredIndexAlreadyExists()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"IxOnlyClustered_{id}";
        var oldIx = $"CIX_Old_{id}";
        var newIx = $"CIX_New_{id}";
        var product = $"IxOnlyClusteredTest_{id}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Arrange: a table whose clustered slot is held by an index NOT in the index-only
            // definition (an "unknown" index that DropUnknownIndexes=0 must not drop wholesale).
            cmd.CommandText = $@"
DROP TABLE IF EXISTS dbo.[{table}];
CREATE TABLE dbo.[{table}] (ColA INT NOT NULL, ColB INT NOT NULL);
CREATE CLUSTERED INDEX [{oldIx}] ON dbo.[{table}] (ColA);";
            cmd.ExecuteNonQuery();

            // Act: index-only quench declaring a DIFFERENT clustered index, DropUnknownIndexes OFF.
            var json = $$"""
            [
              {
                "Schema": "[dbo]",
                "Name": "[{{table}}]",
                "Columns": [
                  { "Name": "[ColA]", "DataType": "INT", "Nullable": false },
                  { "Name": "[ColB]", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                  { "Name": "[{{newIx}}]", "IndexColumns": "[ColB]", "Clustered": true }
                ]
              }
            ]
            """;
            cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{product}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
            Assert.DoesNotThrow(() => TransientRetry.Run(() => cmd.ExecuteNonQuery()),
                "Index-only quench must drop the conflicting clustered index before creating the new one (#302).");

            // Assert: the new clustered index exists (type 1), and the old clustered index is gone
            // (it occupied the clustered slot and had to be dropped to make room).
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WITH (NOLOCK) WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = '{newIx}' AND type = 1";
            Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "New clustered index should exist.");
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WITH (NOLOCK) WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = '{oldIx}'";
            Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old clustered index should have been dropped to free the clustered slot.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }
}
