// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;

namespace SchemaQuench.IntegrationTests.SqlServer;

// SQL Server partition placement (#partitioning, K1). ADOPT AND VERIFY: a table or index is declared onto
// partitioning that ALREADY EXISTS, applied at CREATE, and thereafter compared against the deployed layout
// and REFUSED by name when the two disagree. Nothing here ever emits a statement that moves rows.
//
// That restraint is the whole design, not a shortcut. Moving a table onto or off a partition scheme
// rewrites every row, and a state-based diff cannot derive the SPLIT/MERGE intent behind a changed
// boundary -- it can only see that two layouts differ. So the scheme is a NAME, exactly like FileGroup:
// SchemaSmith places tables on partitioning and never authors or migrates it.
//
// Each test owns a UNIQUE product name so it is scoped to its own tables under parallel execution.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_PartitioningTests : BaseTableQuenchTests
{
    private const string SchemeA = "PS_SchemaSmithTestA";
    private const string SchemeB = "PS_SchemaSmithTestB";
    private const string FunctionA = "PF_SchemaSmithTestA";
    private const string FunctionB = "PF_SchemaSmithTestB";

    // Two functions and two schemes on _mainDb, both mapped ALL TO ([PRIMARY]) -- the test is about
    // PLACEMENT, not about spreading files, and using PRIMARY keeps the fixture free of file management.
    // Idempotent and never dropped, the same "create once" posture the filegroup fixture takes.
    [OneTimeSetUp]
    public void EnsurePartitionSchemesExist()
    {
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        foreach (var (fn, ps) in new[] { (FunctionA, SchemeA), (FunctionB, SchemeB) })
        {
            cmd.CommandText = $@"
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE [name] = '{fn}')
  CREATE PARTITION FUNCTION [{fn}] (INT) AS RANGE RIGHT FOR VALUES (100, 200);
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE [name] = '{ps}')
  CREATE PARTITION SCHEME [{ps}] AS PARTITION [{fn}] ALL TO ([PRIMARY]);";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // ---- create ---------------------------------------------------------------

    [Test]
    public void TableQuench_TableOnPartitionScheme_DeploysAndRoundTrips()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartTableProduct_{uid}";
        var table = $"PartTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeA, "[Id]"), productName: product);

            var extracted = GenerateTable(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(LivePartitionScheme(cmd, table), Is.EqualTo(SchemeA),
                    "the table's data must land on the declared scheme, not the default filegroup");
                Assert.That(LivePartitionColumn(cmd, table), Is.EqualTo("Id"),
                    "the ON clause needs the column the partition function is applied to; the scheme alone "
                    + "is not a placement");
                Assert.That(extracted.PartitionScheme, Is.EqualTo($"[{SchemeA}]"),
                    "and it must round-trip -- a partitioned table that extracts as an ordinary one is the "
                    + "silent loss this feature exists to close");
                Assert.That(extracted.PartitionColumn, Is.EqualTo("[Id]"));
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
    public void TableQuench_IndexAlignedToASchemeOnAnUnpartitionedTable_Deploys()
    {
        // An index is not required to be aligned with its table, and the reverse is equally real: this is
        // an ordinary heap carrying a PARTITIONED index. Inferring index placement from the table would
        // lose the design silently.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartIdxProduct_{uid}";
        var table = $"PartIdxTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithIndexPartitioning(table, SchemeA, "[Id]"), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(LiveIndexPartitionScheme(cmd, table, $"IX_{table}_Id"), Is.EqualTo(SchemeA),
                    "the index must be created on its own declared scheme");
                Assert.That(LivePartitionScheme(cmd, table), Is.Null,
                    "and the table itself must stay unpartitioned -- the index's placement must not leak "
                    + "onto it");
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
    public void TableQuench_RedeployingAPartitionedTable_IsANoOp()
    {
        // The idempotence case, and the one most likely to regress: if the verify path compared the
        // declared scheme against a live value it read differently, every redeploy would either error or
        // churn. It must do neither.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartIdemProduct_{uid}";
        var table = $"PartIdemTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeA, "[Id]"), productName: product);

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeA, "[Id]"), productName: product),
                "an unchanged partitioned table must redeploy cleanly");
            Assert.That(LivePartitionScheme(cmd, table), Is.EqualTo(SchemeA), "and still be on its scheme");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // ---- refusals -------------------------------------------------------------

    [Test]
    public void TableQuench_DeclaredPartitionSchemeDoesNotExist_ThrowsNamingIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartMissingProduct_{uid}";
        var table = $"PartMissingTable_{uid}";
        const string missingScheme = "PS_SchemaSmithTest_DoesNotExist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithTablePartitioning(table, missingScheme, "[Id]"), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the offending table");
                Assert.That(ex.Message, Does.Contain(missingScheme), "and the missing scheme");
                Assert.That(ex.Message, Does.Contain("does not exist"),
                    "SchemaSmith does not create partition functions or schemes any more than it creates "
                    + "filegroups, so this must say so rather than fail generically");
                Assert.That(ObjectExists(cmd, table), Is.False,
                    "and nothing may be created -- falling back to the default filegroup would build the "
                    + "wrong physical layout and report success");
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
    public void TableQuench_PartitionSchemeDeclaredWithoutAColumn_ThrowsNamingBoth()
    {
        // Half a declaration is not a placement. Emitting ON <scheme> with no column is a syntax error
        // whose message names neither the table nor the property, so this is caught before any DDL.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartHalfProduct_{uid}";
        var table = $"PartHalfTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeA, null), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the table");
                Assert.That(ex.Message, Does.Contain("PartitionColumn"),
                    "and the property that is missing, or the user cannot tell which half to add");
                Assert.That(ObjectExists(cmd, table), Is.False, "nothing may be created");
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
    public void TableQuench_BothFileGroupAndPartitionScheme_ThrowsNamingTheContradiction()
    {
        // A table lives on ONE data space. Declaring both is a contradiction the CREATE would otherwise
        // resolve by clause order, quietly honouring whichever was emitted.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartBothProduct_{uid}";
        var table = $"PartBothTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithTableFileGroupAndPartitioning(table, "PRIMARY", SchemeA, "[Id]"), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the table");
                Assert.That(ex.Message, Does.Contain("both"),
                    $"and say the two placements cannot be combined. Got: '{ex.Message}'.");
                Assert.That(ObjectExists(cmd, table), Is.False, "nothing may be created");
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
    public void TableQuench_DeclaredSchemeDiffersFromDeployed_ThrowsAndMovesNothing()
    {
        // The heart of adopt-and-verify. Changing the declared scheme on a live table is a request to
        // rewrite every row; SchemaSmith names both layouts and refuses instead.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartMoveProduct_{uid}";
        var table = $"PartMoveTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeA, "[Id]"), productName: product);
            Assert.That(LivePartitionScheme(cmd, table), Is.EqualTo(SchemeA), "Setup: table should be on SchemeA.");

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeB, "[Id]"), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the table");
                Assert.That(ex.Message, Does.Contain(SchemeB), "the declared scheme");
                Assert.That(ex.Message, Does.Contain(SchemeA), "and the one it is actually on");
                Assert.That(LivePartitionScheme(cmd, table), Is.EqualTo(SchemeA),
                    "and NOTHING may have moved -- a partial move is worse than a refusal");
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
    public void TableQuench_DeclaringASchemeOnATableAlreadyOnAFileGroup_ThrowsAndMovesNothing()
    {
        // Adopting an EXISTING unpartitioned table into partitioning is the same data rewrite, and the
        // refusal has to say which way round the disagreement runs.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartAdoptProduct_{uid}";
        var table = $"PartAdoptTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            Assert.That(ObjectExists(cmd, table), Is.True, "Setup: table should exist unpartitioned.");

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithTablePartitioning(table, SchemeA, "[Id]"), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the table");
                Assert.That(ex.Message, Does.Contain(SchemeA), "and the scheme it was asked to move onto");
                Assert.That(LivePartitionScheme(cmd, table), Is.Null,
                    "the table must NOT have been partitioned -- that rewrites every row");
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
    public void TableQuench_NoPartitioningDeclared_IsCompletelyUnaffected()
    {
        // Backward-compat guard: the state of every existing package. A table declaring no partitioning
        // must deploy, redeploy and extract exactly as it always has, with no new key anywhere.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartUnaffectedProduct_{uid}";
        var table = $"PartUnaffectedTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithTable(table), productName: product),
                "an ordinary table must still redeploy as a no-op");

            var extracted = GenerateTable(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(LivePartitionScheme(cmd, table), Is.Null, "it must stay unpartitioned");
                Assert.That(extracted.PartitionScheme, Is.Null,
                    "and gain no partitioning key -- otherwise every committed .json in the wild churns");
                Assert.That(extracted.PartitionColumn, Is.Null);
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
    public void TableQuench_DeclaredIndexSchemeDoesNotExist_ThrowsNamingIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartIdxMissingProduct_{uid}";
        var table = $"PartIdxMissingTable_{uid}";
        const string missingScheme = "PS_SchemaSmithTest_DoesNotExist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithIndexPartitioning(table, missingScheme, "[Id]"), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"IX_{table}_Id"), "the error must name the offending index");
                Assert.That(ex.Message, Does.Contain(missingScheme), "and the missing scheme");
                Assert.That(ex.Message, Does.Contain("does not exist"), "and say so plainly");
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
    public void TableQuench_DeclaredIndexSchemeDiffersFromDeployed_ThrowsAndMovesNothing()
    {
        // Index placement is deliberately NOT part of the IndexScript string that drives drop-and-recreate,
        // so without an explicit check this change would be silently ignored rather than refused -- the
        // quench would compare two definitions it considers identical and do nothing.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartIdxMoveProduct_{uid}";
        var table = $"PartIdxMoveTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithIndexPartitioning(table, SchemeA, "[Id]"), productName: product);
            Assert.That(LiveIndexPartitionScheme(cmd, table, $"IX_{table}_Id"), Is.EqualTo(SchemeA), "Setup: index on SchemeA.");

            var ex = Assert.Throws<SqlException>(() =>
                RunTableQuenchProc(cmd, WithIndexPartitioning(table, SchemeB, "[Id]"), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"IX_{table}_Id"), "the error must name the index");
                Assert.That(ex.Message, Does.Contain(SchemeB), "the declared scheme");
                Assert.That(ex.Message, Does.Contain(SchemeA), "and the one it is on");
                Assert.That(LiveIndexPartitionScheme(cmd, table, $"IX_{table}_Id"), Is.EqualTo(SchemeA),
                    "and the index must NOT have been rebuilt onto the new scheme");
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

    private static string WithTable(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ]
  }
]
""";

    // partitionColumn may be null, which is the deliberately-half declaration the refusal test uses.
    private static string WithTablePartitioning(string table, string scheme, string partitionColumn)
    {
        var columnJson = partitionColumn is null ? "null" : $"\"{partitionColumn}\"";
        return $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "PartitionScheme": "[{{scheme}}]",
    "PartitionColumn": {{columnJson}},
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ]
  }
]
""";
    }

    private static string WithTableFileGroupAndPartitioning(string table, string fileGroup, string scheme, string partitionColumn) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "FileGroup": "[{{fileGroup}}]",
    "PartitionScheme": "[{{scheme}}]",
    "PartitionColumn": "{{partitionColumn}}",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ]
  }
]
""";

    // An ordinary heap carrying a PARTITIONED nonclustered index.
    private static string WithIndexPartitioning(string table, string scheme, string partitionColumn) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[IX_{{table}}_Id]", "IndexColumns": "[Id]", "PartitionScheme": "[{{scheme}}]", "PartitionColumn": "{{partitionColumn}}" }
    ]
  }
]
""";

    // ---- live-state readers ---------------------------------------------------

    private static bool ObjectExists(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    // The scheme a table's own data sits on (heap/clustered index) -- NULL when it is not partitioned,
    // mirroring the emit-only-when-partitioned extraction contract.
    private static string LivePartitionScheme(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT ds.[name]
  FROM sys.indexes si WITH (NOLOCK)
  JOIN sys.data_spaces ds WITH (NOLOCK) ON ds.data_space_id = si.data_space_id
 WHERE si.[object_id] = OBJECT_ID('dbo.{tableName}')
   AND si.index_id IN (0, 1)
   AND ds.[type] = 'PS'";
        return cmd.ExecuteScalar() as string;
    }

    private static string LivePartitionColumn(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT c.[name]
  FROM sys.indexes si WITH (NOLOCK)
  JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = si.[object_id]
                                         AND ic.index_id = si.index_id
                                         AND ic.partition_ordinal = 1
  JOIN sys.columns c WITH (NOLOCK) ON c.[object_id] = ic.[object_id] AND c.column_id = ic.column_id
 WHERE si.[object_id] = OBJECT_ID('dbo.{tableName}')
   AND si.index_id IN (0, 1)";
        return cmd.ExecuteScalar() as string;
    }

    private static string LiveIndexPartitionScheme(IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"
SELECT ds.[name]
  FROM sys.indexes si WITH (NOLOCK)
  JOIN sys.data_spaces ds WITH (NOLOCK) ON ds.data_space_id = si.data_space_id
 WHERE si.[object_id] = OBJECT_ID('dbo.{tableName}')
   AND si.[name] = '{indexName}'
   AND ds.[type] = 'PS'";
        return cmd.ExecuteScalar() as string;
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
