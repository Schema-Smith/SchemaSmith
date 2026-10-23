// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Coverage for the system-versioned temporal table drop gap surfaced while testing #289: with
// DropTablesRemovedFromProduct enabled, removing a system-versioned temporal table from the product
// aborted with error 13552 ("Drop table operation ... not supported on system-versioned temporal
// tables") because the removed-table drop issued a plain DROP TABLE. SchemaSmith must turn system
// versioning off and drop the history table as well. System-versioned temporal tables are a SQL
// Server feature; PostgreSQL and MySQL have no equivalent, so this is SQL-Server-specific.
//
// Each test owns a UNIQUE product name so DropTablesRemovedFromProduct is scoped to its own tables.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_RemovedTemporalTableTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_DropsRemovedSystemVersionedTemporalTableAndItsHistory()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"RemTemporalProduct_{uniqueId}";
        var table = $"RemTemporal_{uniqueId}";
        var hist = $"{table}_Hist"; // SchemaSmith names the generated history table <Name>_Hist
        var keeper = $"RemTemporalKeeper_{uniqueId}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Create the temporal table (and a keeper) and stamp product ownership.
            RunTableQuenchProc(cmd, WithTemporalAndKeeper(table, keeper), productName: product);
            Assert.That(TemporalType(cmd, table), Is.EqualTo(2), "Setup: table should be system-versioned.");
            Assert.That(ObjectExists(cmd, hist), Is.True, "Setup: history table should exist.");

            // Remove the temporal table from the product, with autodrop on.
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, KeeperOnly(keeper), dropTablesRemovedFromProduct: true, productName: product),
                "Quench must drop a removed system-versioned temporal table (turn versioning off, drop table + history).");

            Assert.Multiple(() =>
            {
                Assert.That(ObjectExists(cmd, table), Is.False, "Removed temporal table should be dropped.");
                Assert.That(ObjectExists(cmd, hist), Is.False, "History table should be dropped too.");
                Assert.That(ObjectExists(cmd, keeper), Is.True, "Kept table must survive.");
            });
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [dbo].[{hist}];
DROP TABLE IF EXISTS [dbo].[{keeper}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string WithTemporalAndKeeper(string table, string keeper) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "IsTemporal": true,
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
  },
  {
    "Schema": "[dbo]",
    "Name": "[{{keeper}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{keeper}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static string KeeperOnly(string keeper) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{keeper}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{keeper}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static int TemporalType(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT CAST(ISNULL(OBJECTPROPERTY(OBJECT_ID('dbo.{tableName}'), 'TableTemporalType'), -1) AS INT)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool ObjectExists(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }
}
