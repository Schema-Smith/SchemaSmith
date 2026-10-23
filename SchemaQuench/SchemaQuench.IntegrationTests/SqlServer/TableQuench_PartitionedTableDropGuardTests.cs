// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Data-loss guard coverage: drop-by-absence has no partition awareness, so a product-owned table
// that grows and gets manually partitioned (SchemaSmith has no partitioning support of its own)
// looks like an ordinary drop-by-absence candidate once removed from the package. The guard in
// SchemaSmith.ModifiedTableQuench.sql fails closed rather than destroying partitioned data.
//
// Each test owns a UNIQUE product name so DropTablesRemovedFromProduct is scoped to its own tables
// and never drops a sibling test's tables under parallel execution.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PartitionedTableDropGuardTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_PartitionedOwnedTableRemovedFromProduct_IsNotDroppedAndRunFails()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartGuardProduct_{uid}";
        var table = $"PartGuardTable_{uid}";
        var keep = $"PartGuardKeep_{uid}"; // anchor table that stays in the package across both quenches

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Establish product ownership of an ordinary table.
            RunTableQuenchProc(cmd, WithTables(table, keep), productName: product);
            Assert.That(ObjectExists(cmd, table), Is.True, "Setup: table should exist after the first quench.");

            // Manually partition the owned table -- exactly why anyone partitions: it grew.
            // SchemaSmith has no partitioning support, so this can only happen by hand.
            PartitionTable(cmd, table, uid);
            Assert.That(IsPartitioned(cmd, table), Is.True, "Setup: table should be partitioned.");

            // Remove the table from the product with autodrop on -- the guard must abort instead
            // of destroying the partitioned table.
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithTable(keep), dropTablesRemovedFromProduct: true, productName: product),
                "The partition guard must fail the run instead of dropping a partitioned table.");
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "Failure message must name the offending table.");
                Assert.That(ex.Message, Does.Contain("PreventDrop"), "Failure message must tell the operator how to proceed (PreventDrop or manual drop).");
                Assert.That(ObjectExists(cmd, table), Is.True, "Guard must prevent the drop: the partitioned table must still exist.");
            });
        }
        finally
        {
            CleanupPartitionedTable(cmd, table, uid);
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{keep}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // The guard must not over-reach: an ordinary (non-partitioned) owned table removed from the
    // product must still be dropped by absence exactly as before.
    [Test]
    public void TableQuench_OrdinaryOwnedTableRemovedFromProduct_IsStillDropped()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartGuardOrdinaryProduct_{uid}";
        var table = $"PartGuardOrdinaryTable_{uid}";
        var keep = $"PartGuardOrdinaryKeep_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTables(table, keep), productName: product);
            Assert.That(ObjectExists(cmd, table), Is.True, "Setup: table should exist after the first quench.");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithTable(keep), dropTablesRemovedFromProduct: true, productName: product),
                "A non-partitioned table must still be dropped by absence -- the guard must not over-reach.");
            Assert.That(ObjectExists(cmd, table), Is.False, "Ordinary owned table removed from the product must be dropped.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]; DROP TABLE IF EXISTS [dbo].[{keep}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // A partitioned table that is still present in the package must be entirely unaffected by the
    // guard -- it only inspects tables actually selected for drop-by-absence.
    [Test]
    public void TableQuench_PartitionedTableStillPresentInProduct_IsUnaffected()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartGuardKeptProduct_{uid}";
        var table = $"PartGuardKeptTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            PartitionTable(cmd, table, uid);
            Assert.That(IsPartitioned(cmd, table), Is.True, "Setup: table should be partitioned.");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithTable(table), dropTablesRemovedFromProduct: true, productName: product),
                "A partitioned table still declared in the product must not trip the drop-by-absence guard.");
            Assert.That(ObjectExists(cmd, table), Is.True, "Table still in the product must survive untouched.");
        }
        finally
        {
            CleanupPartitionedTable(cmd, table, uid);
        }
        conn.Close();
    }


    // The filegroup move-guard resolves the deployed data space and branches on its type: a partitioned
    // table sits on a partition SCHEME, so there is no single filegroup to compare against. Leaving
    // FileGroup unset is supported (covered above); declaring one is a placement SchemaSmith cannot honour,
    // so it must fail closed with a message that names the scheme rather than reporting a null filegroup.
    [Test]
    public void TableQuench_PartitionedTableDeclaringFileGroup_FailsClosed()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartGuardFgProduct_{uid}";
        var table = $"PartGuardFgTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            PartitionTable(cmd, table, uid);
            Assert.That(IsPartitioned(cmd, table), Is.True, "Setup: table should be partitioned.");

            var ex = Assert.Throws<SqlException>(
                () => RunTableQuenchProc(cmd, WithTableOnFileGroup(table, "[PRIMARY]"), productName: product),
                "Declaring a filegroup on a partitioned table must fail closed, not be silently ignored.");
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "Failure message must name the offending table.");
                Assert.That(ex.Message, Does.Contain($"ps_{uid}"), "Failure message must name the partition scheme it is deployed on.");
                Assert.That(ex.Message, Does.Not.Contain("(null)"), "Failure message must not report a null filegroup.");
            });
        }
        finally
        {
            CleanupPartitionedTable(cmd, table, uid);
        }
        conn.Close();
    }

    private static void PartitionTable(IDbCommand cmd, string table, string uid)
    {
        cmd.CommandText = $"CREATE PARTITION FUNCTION [pf_{uid}](int) AS RANGE LEFT FOR VALUES (1000000);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE PARTITION SCHEME [ps_{uid}] AS PARTITION [pf_{uid}] ALL TO ([PRIMARY]);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE UNIQUE CLUSTERED INDEX [PK_{table}] ON [dbo].[{table}]([Id]) WITH (DROP_EXISTING = ON) ON [ps_{uid}]([Id]);";
        cmd.ExecuteNonQuery();
    }

    private static void CleanupPartitionedTable(IDbCommand cmd, string table, string uid)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE [name] = 'ps_{uid}') DROP PARTITION SCHEME [ps_{uid}];";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE [name] = 'pf_{uid}') DROP PARTITION FUNCTION [pf_{uid}];";
        cmd.ExecuteNonQuery();
    }

    private static string WithTable(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static string WithTableOnFileGroup(string table, string fileGroup) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "FileGroup": "{{fileGroup}}",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static string WithTables(string table, string keep) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  },
  {
    "Schema": "[dbo]",
    "Name": "[{{keep}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{keep}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static bool ObjectExists(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static bool IsPartitioned(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.partitions WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND index_id < 2";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 1;
    }
}
