// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// SQL Server memory-optimized (Hekaton) tables (#J1). A distinct in-memory storage engine, so its indexes
// must be declared INLINE in the CREATE TABLE -- CREATE INDEX is rejected on such a table -- and
// MEMORY_OPTIMIZED / DURABILITY cannot be ALTERed at all, so a change is refused by name, like GraphType.
//
// This fixture owns a DEDICATED database: memory-optimized tables cannot be created on a CDC-enabled
// database (they add ALTER/DROP triggers the engine forbids), and the shared _mainDb has CDC turned on by
// its own tests. So a clean database is created, kindled, and given a MEMORY_OPTIMIZED_DATA filegroup here.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_MemoryOptimizedTests : BaseTableQuenchTests
{
    private string _db = null!;

    [OneTimeSetUp]
    public void CreateMemoryOptimizedDatabase()
    {
        _db = $"SchemaMemOpt_{Guid.NewGuid():N}"[..40];

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "SELECT CONVERT(INT, ISNULL(SERVERPROPERTY('IsXTPSupported'), 0))";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1)
            Assert.Ignore("This SQL Server edition does not support memory-optimized tables (IsXTPSupported = 0).");

        cmd.CommandText = $"CREATE DATABASE [{_db}]";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $@"
DECLARE @v_Path NVARCHAR(500) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID('{_db}') AND file_id = 1);
ALTER DATABASE [{_db}] ADD FILEGROUP [MOD_fg] CONTAINS MEMORY_OPTIMIZED_DATA;
EXEC('ALTER DATABASE [{_db}] ADD FILE (NAME = ''MOD_container'', FILENAME = ''' + @v_Path + 'MOD_container'') TO FILEGROUP [MOD_fg]');";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_db);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
        conn.Close();
    }

    [OneTimeTearDown]
    public void DropMemoryOptimizedDatabase()
    {
        if (_db == null) return;
        try
        {
            using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _db);
        }
        catch (SqlException) { /* teardown must not mask an assertion */ }
    }

    // SQL Server refuses MERGE on a memory-optimized table in every mode, so delivery to one always failed. It now takes
    // separate UPDATE, INSERT and DELETE statements, with the SNAPSHOT hint a memory-optimized table needs inside an
    // explicit transaction -- run here both ways.
    [TestCase(false, TestName = "Delivery_ToAMemoryOptimizedTable_Merges")]
    [TestCase(true, TestName = "Delivery_ToAMemoryOptimizedTable_MergesInsideATransaction")]
    public void Delivery_ToAMemoryOptimizedTable(bool inTransaction)
    {
        var table = $"MoData_{Guid.NewGuid():N}"[..20];
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, V NVARCHAR(20) NULL) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA);";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $"INSERT dbo.[{table}] (Id, V) VALUES (1, 'a'), (9, 'z');";
            cmd.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", table,
                @"[{""Id"":1,""V"":""b""},{""Id"":2,""V"":""c""}]", "[Id]",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            using (var tx = inTransaction ? conn.BeginTransaction() : null)
            {
                cmd.Transaction = tx;
                cmd.CommandText = script;
                cmd.ExecuteNonQuery();
                tx?.Commit();
                cmd.Transaction = null;
            }

            cmd.CommandText = $"SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.[{table}]";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1:b,2:c"));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{table}]";
            cmd.ExecuteNonQuery();
        }
    }

    // An append-only ledger table refuses UPDATE and DELETE, so a merge carrying either failed. Delivery to one inserts
    // the rows it lacks and reports, without changing them, the rows that differ or would have been deleted.
    [Test]
    public void Delivery_ToAnAppendOnlyLedgerTable_InsertsAndWarns()
    {
        var table = $"LedgerData_{Guid.NewGuid():N}"[..24];
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)), 4) AS INT)";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 16) Assert.Ignore("Ledger tables need SQL Server 2022.");
        cmd.CommandText = $"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, V NVARCHAR(20) NULL) WITH (LEDGER = ON (APPEND_ONLY = ON));";
        cmd.ExecuteNonQuery();
        var messages = new List<string>();
        conn.InfoMessage += (_, e) => messages.Add(e.Message);
        try
        {
            cmd.CommandText = $"INSERT dbo.[{table}] (Id, V) VALUES (1, 'a'), (9, 'z');";
            cmd.ExecuteNonQuery();

            cmd.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", table,
                @"[{""Id"":1,""V"":""b""},{""Id"":2,""V"":""c""}]", "[Id]",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.[{table}]";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1:a,2:c,9:z"), "new rows inserted, existing rows untouched");
            Assert.That(string.Join("\n", messages), Does.Contain(table).And.Contain("append-only"),
                "the rows left unchanged are reported: " + string.Join(" | ", messages));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{table}]";
            cmd.ExecuteNonQuery();
        }
    }

    private SqlConnection Open()
    {
        var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_db);
        return conn;
    }

    [Test]
    public void MemoryOptimizedTable_DeploysWithInlineIndexes_AndRoundTrips()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOProduct_{uid}";
        var table = $"MOTable_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product);

            var extracted = GenerateTable(cmd, table);
            Assert.Multiple(() =>
            {
                cmd.CommandText = $"SELECT is_memory_optimized FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}')";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "the table must actually be memory-optimized -- an inline-index CREATE that silently fell "
                    + "back to a disk table would deploy green and be wrong");
                cmd.CommandText = $"SELECT durability_desc FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}')";
                Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("SCHEMA_AND_DATA"));

                cmd.CommandText = $@"SELECT hi.bucket_count FROM sys.hash_indexes hi WHERE hi.[object_id] = OBJECT_ID('dbo.{table}')";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1024),
                    "the hash PK must be created inline with its bucket count");

                cmd.CommandText = $@"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'IX_{table}_Val'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the secondary index must be created inline");

                Assert.That(extracted.MemoryOptimized, Is.True, "extraction must round-trip the memory-optimized flag");
                Assert.That(extracted.Durability, Is.EqualTo("SCHEMA_AND_DATA"));
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void SchemaOnlyDurability_DeploysAndRoundTrips()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOSchemaOnlyProduct_{uid}";
        var table = $"MOSchemaOnlyTable_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_ONLY", bucketCount: 512), productName: product);
            var extracted = GenerateTable(cmd, table);
            Assert.Multiple(() =>
            {
                cmd.CommandText = $"SELECT durability_desc FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}')";
                Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("SCHEMA_ONLY"),
                    "SCHEMA_ONLY must reach the engine -- getting durability wrong silently changes whether "
                    + "the rows survive a restart");
                Assert.That(extracted.Durability, Is.EqualTo("SCHEMA_ONLY"));
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void RedeployingAMemoryOptimizedTable_IsANoOp()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOIdemProduct_{uid}";
        var table = $"MOIdemTable_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product),
                "an unchanged memory-optimized table must redeploy cleanly -- the inline indexes already "
                + "exist, so the ordinary index pass must find them present rather than trying CREATE INDEX");
            cmd.CommandText = $"SELECT is_memory_optimized FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void ChangingMemoryOptimizedOnADeployedTable_IsRefused()
    {
        // There is no ALTER TABLE ... SET (MEMORY_OPTIMIZED = ON/OFF) -- it is error 102 -- so converting a
        // table in either direction is impossible. The change is refused by name, like GraphType/Ledger.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOConvertProduct_{uid}";
        var table = $"MOConvertTable_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithDiskTable(table), productName: product);

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the refusal must name the table");
                Assert.That(ex.Message, Does.Contain("memory"), $"and the state it refuses to change. Got: '{ex.Message}'.");
                cmd.CommandText = $"SELECT is_memory_optimized FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}')";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0),
                    "and the table must NOT have been converted -- it stays the disk table it was");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void MemoryOptimizedTable_RemovedFromProduct_IsDroppedAndOwnershipPruned()
    {
        // A memory-optimized table's ownership lives in SchemaSmith.ProductOwnership (it cannot carry the
        // ProductName extended property). This proves the full ownership lifecycle works off that table:
        // the removed table is recognised as product-owned, dropped, AND its ownership row is pruned.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MODropProduct_{uid}";
        var anchor = $"MOAnchor_{uid}";   // a regular table keeps dbo in #SchemaList after the mem-opt table is removed
        var mo = $"MODrop_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, TwoTablePackage(anchor, mo, preventDrop: false), productName: product);
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ProductOwnership WHERE [TableName] = '{mo}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "deploy must record ownership for the mem-opt table");

            RunTableQuenchProc(cmd, AnchorOnlyPackage(anchor), productName: product, dropTablesRemovedFromProduct: true);

            Assert.Multiple(() =>
            {
                cmd.CommandText = $"SELECT OBJECT_ID('dbo.{mo}')";
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value),
                    "a mem-opt table removed from the product must be dropped -- proving ProductOwnership scoped it as owned");
                cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ProductOwnership WHERE [TableName] = '{mo}'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0),
                    "and its ownership row must be pruned -- a stale row would resurrect the table as 'removed' every later deploy");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{mo}]; DROP TABLE IF EXISTS [dbo].[{anchor}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void MemoryOptimizedTable_ProtectedByPreventDrop_SurvivesRemoval()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOKeepProduct_{uid}";
        var anchor = $"MOKeepAnchor_{uid}";
        var mo = $"MOKeep_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, TwoTablePackage(anchor, mo, preventDrop: true), productName: product);
            RunTableQuenchProc(cmd, AnchorOnlyPackage(anchor), productName: product, dropTablesRemovedFromProduct: true);

            Assert.Multiple(() =>
            {
                cmd.CommandText = $"SELECT OBJECT_ID('dbo.{mo}')";
                Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value),
                    "PreventDrop must protect the mem-opt table from drop-by-absence, exactly as it does an extended-property-tracked table");
                cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ProductOwnership WHERE [TableName] = '{mo}'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "and the ownership row survives, because the table still exists (the prune keys off catalog existence)");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{mo}]; DROP TABLE IF EXISTS [dbo].[{anchor}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void MemoryOptimizedTable_OwnedByAnotherProduct_IsRefused()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var productA = $"MOOwnerA_{uid}";
        var productB = $"MOOwnerB_{uid}";
        var mo = $"MOOwned_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithMemoryOptimizedTable(mo, "SCHEMA_AND_DATA", bucketCount: 1024), productName: productA);

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithMemoryOptimizedTable(mo, "SCHEMA_AND_DATA", bucketCount: 1024), productName: productB));
            Assert.That(ex!.Message, Does.Contain("owned by another product"),
                "the ownership row folded in from ProductOwnership must make the cross-product guard fire for a mem-opt table too");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{mo}]; DELETE FROM SchemaSmith.ProductOwnership WHERE [TableName] = '{mo}';";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void ChangingBucketCountOnADeployedMemoryOptimizedTable_IsRefused()
    {
        // Memory-optimized inline indexes cannot be dropped/recreated by SchemaSmith's ordinary index
        // convergence (CREATE/DROP INDEX is rejected on a memory-optimized table), so a bucket-count
        // change cannot be applied. Rather than silently ignore the declared change, SchemaSmith refuses
        // it by name -- consistent with the MemoryOptimized/Durability refusals. Migrate by recreating.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOBucketProduct_{uid}";
        var table = $"MOBucket_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product);

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 4096), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the refusal must name the table");
                Assert.That(ex.Message, Does.Contain("bucket count").IgnoreCase.Or.Contain("inline index"),
                    $"and say why. Got: '{ex.Message}'.");
                cmd.CommandText = $"SELECT hi.bucket_count FROM sys.hash_indexes hi WHERE hi.[object_id] = OBJECT_ID('dbo.{table}')";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1024),
                    "and the deployed bucket count is unchanged -- nothing was altered");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void AddingAnInlineIndexToADeployedMemoryOptimizedTable_IsRefused()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOAddIxProduct_{uid}";
        var table = $"MOAddIx_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Deploy with just the hash PK, then redeploy declaring an extra secondary index.
            RunTableQuenchProc(cmd, OneIndexMemOptTable(table), productName: product);

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the refusal must name the table");
                cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'IX_{table}_Val'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0),
                    "and the secondary index must NOT have been added -- the declared inline-index change is refused, not partially applied");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // ---- package builders -----------------------------------------------------

    private static string TwoTablePackage(string anchor, string mo, bool preventDrop) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{anchor}}]",
    "Columns": [
      { "Name": "[Id]", "DataType": "INT", "Nullable": false }
    ],
    "Indexes": [
      { "Name": "[PK_{{anchor}}]", "PrimaryKey": true, "IndexColumns": "[Id]" }
    ]
  },
  {
    "Schema": "[dbo]",
    "Name": "[{{mo}}]",
    "MemoryOptimized": true,
    "Durability": "SCHEMA_AND_DATA",
    "PreventDrop": {{(preventDrop ? "true" : "false")}},
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT",         "Nullable": false },
      { "Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{mo}}]", "PrimaryKey": true, "IndexColumns": "[Id]", "BucketCount": 1024 }
    ]
  }
]
""";

    private static string AnchorOnlyPackage(string anchor) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{anchor}}]",
    "Columns": [
      { "Name": "[Id]", "DataType": "INT", "Nullable": false }
    ],
    "Indexes": [
      { "Name": "[PK_{{anchor}}]", "PrimaryKey": true, "IndexColumns": "[Id]" }
    ]
  }
]
""";

    private static string WithDiskTable(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT",          "Nullable": false },
      { "Name": "[Val]", "DataType": "NVARCHAR(50)",  "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" }
    ]
  }
]
""";

    // SS-125. 2014's In-Memory OLTP takes no foreign key, CHECK, UNIQUE or (max) column on a memory-optimized table (2016)
    // and no computed column (2017), and cannot alter one afterwards. Only an Enterprise 2014 can host the table at all
    // (Express refuses it with 534, probed), so the gate runs here under the version override, before any DDL, and the
    // server never sees the statement.
    [TestCase(12, new[] { "foreign keys", "CHECK constraints", "UNIQUE indexes or constraints", "max-length columns", "computed columns" })]
    [TestCase(13, new[] { "computed columns" })]
    public void AMemoryOptimizedTableDeclaringWhatTheVersionLacks_IsRefusedByName(int major, string[] named)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOGateProduct_{uid}";
        var table = $"MOGate_{uid}";
        var json = $$"""
[
  { "Schema": "[dbo]", "Name": "[{{table}}_P]", "MemoryOptimized": true, "Durability": "SCHEMA_AND_DATA",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{table}}_P]", "PrimaryKey": true, "IndexColumns": "[Id]" } ] },
  { "Schema": "[dbo]", "Name": "[{{table}}]", "MemoryOptimized": true, "Durability": "SCHEMA_AND_DATA",
    "Columns": [
      { "Name": "[Id]", "DataType": "INT", "Nullable": false },
      { "Name": "[ParentId]", "DataType": "INT", "Nullable": false },
      { "Name": "[Code]", "DataType": "INT", "Nullable": false, "CheckExpression": "[Code] > 0" },
      { "Name": "[Notes]", "DataType": "NVARCHAR(MAX)", "Nullable": true },
      { "Name": "[Twice]", "DataType": "INT", "ComputedExpression": "[Code] * 2" }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" },
      { "Name": "[UX_{{table}}_Code]", "Unique": true, "IndexColumns": "[Code]" }
    ],
    "ForeignKeys": [ { "Name": "[FK_{{table}}_P]", "Columns": "[ParentId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[{{table}}_P]", "RelatedColumns": "[Id]" } ] }
]
""";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"SET CONTEXT_INFO 0x53534F56{major:X8}";
            cmd.ExecuteNonQuery();
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json, productName: product));
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.tables WHERE [name] LIKE 'MOGate[_]{uid}%'";
            var created = Convert.ToInt32(cmd.ExecuteScalar());
            // The table's own list, "[dbo].[t] (a, b)": the message's closing advice names computed columns too.
            var marker = $"dbo].[{table}] (";
            var at = ex!.Message.IndexOf(marker, StringComparison.Ordinal);
            var listed = at < 0 ? "" : ex.Message[(at + marker.Length)..ex.Message.IndexOf(')', at + marker.Length)];
            Assert.Multiple(() =>
            {
                Assert.That(ex.Message, Does.Contain($"SQL Server {major}"));
                Assert.That(listed.Split(", "), Is.EquivalentTo(named),
                    $"the table's list was '{listed}' in: {ex.Message}");
                Assert.That(ex.Message, Does.Not.Contain($"dbo].[{table}_P]"), "the parent declares none of them");
                Assert.That(created, Is.Zero, "refused before any DDL");
            });
        }
        finally
        {
            cmd.CommandText = $"SET CONTEXT_INFO 0x0; DROP TABLE IF EXISTS [dbo].[{table}]; DROP TABLE IF EXISTS [dbo].[{table}_P];";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AMemoryOptimizedTableDeclaringNoneOfThem_StillDeploysUnderThe2014Gate()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"MOGateOkProduct_{uid}";
        var table = $"MOGateOk_{uid}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = "SET CONTEXT_INFO 0x53534F560000000C";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, WithMemoryOptimizedTable(table, "SCHEMA_AND_DATA", bucketCount: 1024), productName: product);
            cmd.CommandText = $"SELECT is_memory_optimized FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));
        }
        finally
        {
            cmd.CommandText = $"SET CONTEXT_INFO 0x0; DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
    }

    private static string WithMemoryOptimizedTable(string table, string durability, int bucketCount) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "MemoryOptimized": true,
    "Durability": "{{durability}}",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT",          "Nullable": false },
      { "Name": "[Val]", "DataType": "NVARCHAR(50)",  "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]", "BucketCount": {{bucketCount}} },
      { "Name": "[IX_{{table}}_Val]", "IndexColumns": "[Val]" }
    ]
  }
]
""";

    private static string OneIndexMemOptTable(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "MemoryOptimized": true,
    "Durability": "SCHEMA_AND_DATA",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT",         "Nullable": false },
      { "Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]", "BucketCount": 1024 }
    ]
  }
]
""";

    // SS-055. A memory-optimized table built by hand -- a hash primary key, a unique constraint, a unique index, a range
    // index and a clustered columnstore -- must deploy from its own extraction and then redeploy as a no-op. The inline
    // builder wrote the columnstore as NONCLUSTERED (), a syntax error, and wrote the unique constraint as an index.
    [Test]
    public void AHandBuiltMemoryOptimizedTable_DeploysFromItsOwnExtraction_AndRedeploysUnchanged()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"MOHand_{uid}";
        var product = $"MOHandProduct_{uid}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $@"CREATE TABLE dbo.{table} (
                Id INT NOT NULL CONSTRAINT PK_{table} PRIMARY KEY NONCLUSTERED HASH WITH (BUCKET_COUNT = 64),
                Code INT NOT NULL, Ref INT NOT NULL, Val INT NULL,
                CONSTRAINT UQ_{table}_Code UNIQUE NONCLUSTERED (Code),
                INDEX UX_{table}_Ref UNIQUE NONCLUSTERED (Ref),
                INDEX IX_{table}_Val NONCLUSTERED (Val),
                INDEX CCI_{table} CLUSTERED COLUMNSTORE) WITH (MEMORY_OPTIMIZED = ON)";
            cmd.ExecuteNonQuery();
            var built = IndexShape(cmd, table);
            var package = "[" + GenerateTableJsonText(cmd, table) + "]";

            cmd.CommandText = $"DROP TABLE dbo.{table}";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, package, productName: product);
            Assert.That(IndexShape(cmd, table), Is.EqualTo(built), "the first deploy of the extraction must create the same indexes");

            RunTableQuenchProc(cmd, package, productName: product);
            Assert.That(IndexShape(cmd, table), Is.EqualTo(built), "and the redeploy must find nothing to change");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
    }

    // Each index as name:kind:unique:constraint, in name order.
    private static string IndexShape(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT STRING_AGG(CONCAT(i.[name] COLLATE DATABASE_DEFAULT, ':', i.type_desc COLLATE DATABASE_DEFAULT, ':', i.is_unique, ':', i.is_unique_constraint | i.is_primary_key), ';')
                                    WITHIN GROUP (ORDER BY i.[name])
                               FROM sys.indexes i WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.index_id > 0";
        return cmd.ExecuteScalar() as string;
    }

    private static string GenerateTableJsonText(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var json = new StringBuilder();
        while (reader.Read()) json.Append(reader.GetString(0)).Append("\r\n");
        return json.ToString();
    }

    private static SqlServerTable GenerateTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var json = string.Empty;
        while (reader.Read()) json += $"{reader.GetString(0)}\r\n";
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
    }
}
