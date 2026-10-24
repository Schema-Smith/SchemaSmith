// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Data-loss guard coverage: drop-by-absence has no partition awareness, so a product-owned table
// that grows and gets manually partitioned (SchemaSmith has no partitioning support of its own)
// looks like an ordinary drop-by-absence candidate once removed from the package. The guard in
// SchemaSmith.ModifiedTableQuench.sql fails closed rather than destroying partitioned data.
//
// PostgreSQL cannot ALTER an existing plain table into a partitioned parent in place, so the
// realistic "manually partitioned after the fact" path is ATTACHing the owned table as a child
// partition of a NEW parent -- it keeps the table's identity (and its data) while relispartition
// flips true. This is exactly the case the guard's relispartition check exists for.
//
// Each test owns a UNIQUE product name so DropTablesRemovedFromProduct is scoped to its own tables
// and never drops a sibling test's tables under parallel execution.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PartitionedTableDropGuardTests : BaseTableQuenchTests
{
    private const string Schema = "public";

    [Test]
    public void TableQuench_PartitionedOwnedTableRemovedFromProduct_IsNotDroppedAndRunFails()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartGuardProduct_{uid}";
        var table = $"PartGuardTable_{uid}";
        var keep = $"PartGuardKeep_{uid}"; // anchor table that stays in the package across both quenches
        var parent = $"PartGuardParent_{uid}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTables(table, keep), productName: product);
            Assert.That(ObjectExists(cmd, table), Is.True, "Setup: table should exist after the first quench.");

            // Manually partition the owned table by ATTACHing it as a child partition -- the only
            // way to turn an existing plain table into one on PostgreSQL.
            AttachAsPartition(cmd, table, parent);
            Assert.That(IsPartition(cmd, table), Is.True, "Setup: table should now be a child partition.");

            var ex = Assert.Throws<PostgresException>(() => RunTableQuenchProc(cmd, WithTable(keep), dropTablesRemovedFromProduct: true, productName: product),
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
            CleanupPartition(cmd, table, parent, keep);
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

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{Schema}"".""{table}""; DROP TABLE IF EXISTS ""{Schema}"".""{keep}"";";
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
        var parent = $"PartGuardKeptParent_{uid}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            AttachAsPartition(cmd, table, parent);
            Assert.That(IsPartition(cmd, table), Is.True, "Setup: table should now be a child partition.");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithTable(table), dropTablesRemovedFromProduct: true, productName: product),
                "A partitioned table still declared in the product must not trip the drop-by-absence guard.");
            Assert.That(ObjectExists(cmd, table), Is.True, "Table still in the product must survive untouched.");
        }
        finally
        {
            CleanupPartition(cmd, table, parent, keep: null);
        }
        conn.Close();
    }

    private static void AttachAsPartition(IDbCommand cmd, string table, string parent)
    {
        cmd.CommandText = $@"CREATE TABLE ""{Schema}"".""{parent}"" (""Id"" integer NOT NULL) PARTITION BY RANGE (""Id"");";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $@"ALTER TABLE ""{Schema}"".""{parent}"" ATTACH PARTITION ""{Schema}"".""{table}"" FOR VALUES FROM (MINVALUE) TO (MAXVALUE);";
        cmd.ExecuteNonQuery();
    }

    private static void CleanupPartition(IDbCommand cmd, string table, string parent, string keep)
    {
        // Dropping the parent also drops its attached partition, taking the (now child) table with it.
        cmd.CommandText = $@"DROP TABLE IF EXISTS ""{Schema}"".""{parent}"" CASCADE;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $@"DROP TABLE IF EXISTS ""{Schema}"".""{table}"";";
        cmd.ExecuteNonQuery();
        if (keep != null)
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{Schema}"".""{keep}"";";
            cmd.ExecuteNonQuery();
        }
    }

    private static string WithTable(string table) => $$"""
[
  {
    "Schema": "public",
    "Name": "{{table}}",
    "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ],
    "Indexes": [ { "Name": "PK_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
  }
]
""";

    private static string WithTables(string table, string keep) => $$"""
[
  {
    "Schema": "public",
    "Name": "{{table}}",
    "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ],
    "Indexes": [ { "Name": "PK_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
  },
  {
    "Schema": "public",
    "Name": "{{keep}}",
    "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ],
    "Indexes": [ { "Name": "PK_{{keep}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
  }
]
""";

    private static bool ObjectExists(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT to_regclass('\"{Schema}\".\"{tableName}\"') IS NOT NULL";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static bool IsPartition(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT c.relispartition
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = '{Schema}'
 WHERE c.relname = '{tableName}'";
        return (bool)cmd.ExecuteScalar()!;
    }
}
