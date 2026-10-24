// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Backlog E3: COLUMN_SET FOR ALL_SPARSE_COLUMNS. SQL Server only allows adding a column set to a
// table when either (a) it is created fresh, or (b) the sparse columns it aggregates are added in
// the SAME statement -- a column set cannot be added by ALTER TABLE to a table that already has
// standalone sparse columns. SchemaSmith already batches a table's new columns into one CREATE
// TABLE / ALTER TABLE ADD (SchemaSmith.MissingTableAndColumnQuench.sql), so both cases are covered
// by construction; no pre-validation of the sparse/column-set relationship was added -- an illegal
// combination is left to the engine's own (specific) rejection.
//
// The reliable no-rebuild signal for idempotency is the procedure's own log: when a column is
// classified as modified, ModifiedTableQuench emits a "  Altering Column <schema>.<table>.<column>"
// RAISERROR (captured here via SqlConnection.InfoMessage). A converged table must emit none.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ColumnSetTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldCreateTableWithSparseColumnsAndColumnSet()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColSetNew_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[SparseA]", "DataType": "VARCHAR(20)", "Nullable": true, "Sparse": true},
        {"Name": "[SparseB]", "DataType": "INT", "Nullable": true, "Sparse": true},
        {"Name": "[Aggregated]", "DataType": "XML", "Nullable": true, "IsColumnSet": true}
    ]
}
""";

        try
        {
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $"SELECT [name], is_sparse, is_column_set FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') ORDER BY column_id";
            using (var reader = cmd.ExecuteReader())
            {
                var rows = new Dictionary<string, (bool IsSparse, bool IsColumnSet)>();
                while (reader.Read())
                    rows[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetBoolean(2));

                Assert.Multiple(() =>
                {
                    Assert.That(rows["SparseA"].IsSparse, Is.True, "SparseA must be created SPARSE");
                    Assert.That(rows["SparseA"].IsColumnSet, Is.False);
                    Assert.That(rows["SparseB"].IsSparse, Is.True, "SparseB must be created SPARSE");
                    Assert.That(rows["SparseB"].IsColumnSet, Is.False);
                    Assert.That(rows["Aggregated"].IsColumnSet, Is.True, "Aggregated must be created as the column set");
                    Assert.That(rows["Aggregated"].IsSparse, Is.False, "the column set column is not itself sparse");
                });
            }
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    [Test]
    public void ShouldAddSparseColumnsAndColumnSetToExistingTableInOneAlter()
    {
        // Exercises MissingTableAndColumnQuench's ADD-columns batching: a column set cannot be
        // added by ALTER TABLE to a table that already has standalone sparse columns, but IS legal
        // when the column set and the sparse columns it aggregates arrive in the same ALTER TABLE
        // ADD statement -- which is exactly what this proc emits (one comma-separated ADD per
        // table, not one ALTER per column).
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColSetAdd_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE TABLE dbo.{tableName} (Id INT NOT NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[SparseA]", "DataType": "VARCHAR(20)", "Nullable": true, "Sparse": true},
        {"Name": "[Aggregated]", "DataType": "XML", "Nullable": true, "IsColumnSet": true}
    ]
}
""";

        try
        {
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $"SELECT is_column_set FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'Aggregated'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "the column set must be added alongside its sparse column in the same ALTER TABLE ADD");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    [Test]
    public void ShouldNotAlterColumnSetOrSparseColumnsOnReQuenchWithNoChanges()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColSetIdem_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[SparseA]", "DataType": "VARCHAR(20)", "Nullable": true, "Sparse": true},
        {"Name": "[Aggregated]", "DataType": "XML", "Nullable": true, "IsColumnSet": true}
    ],
    "Indexes": [
        { "Name": "[{{pkName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
    ]
}
""";

        try
        {
            // Converge once.
            RunTableQuenchProc(cmd, json);

            // No-op re-quench: capture only the messages from THIS pass.
            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var alterMessages = messages.FindAll(m => m.Contains("Altering Column") && m.Contains(tableName));
            Assert.That(alterMessages, Is.Empty,
                "Re-quench of an unchanged sparse/column-set table must NOT re-alter any column. " +
                "An 'Altering Column' message means the Sparse/IsColumnSet drift comparison produced a phantom modification. " +
                $"Messages: {string.Join(" | ", messages)}");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void ShouldConvertExistingPlainColumnIntoColumnSetWhenNoOtherSparseColumnsInvolved()
    {
        // The ACHIEVABLE conversion shape: the quench's only column-set-relevant change is the
        // conversion itself -- no new sparse columns are being added in this same quench, and the
        // table has no pre-existing sparse columns from an earlier deploy. ModifiedTableQuench drops
        // the plain column and re-adds it via its own "Add Missing Physical Columns" step
        // (SchemaSmith.ModifiedTableQuench.sql:1089-1095); at that point the table has ZERO sparse
        // columns, so "add a column set to a table with no sparse columns" (legal per Microsoft's
        // docs) is exactly what happens. No phase-ordering contortion needed -- this works today.
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColSetConvertAlone_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE TABLE dbo.{tableName} (Id INT NOT NULL, PlainXml XML NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[PlainXml]", "DataType": "XML", "Nullable": true, "IsColumnSet": true}
    ]
}
""";

        try
        {
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $"SELECT is_column_set FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'PlainXml'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "PlainXml must be converted into the column set via drop+recreate when no sparse columns are involved");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    [Test]
    public void ShouldRejectConvertingExistingColumnIntoColumnSetWhenNewSparseColumnAddedInSameQuench()
    {
        // The LIMITATION: converting an existing plain column into a column set in the same quench
        // that also adds a brand-new sparse column always fails, because SchemaSmith's two quench
        // phases are strictly sequential, separate statements -- never batched together:
        //   1. SchemaSmith.TableQuench.sql:24 EXECs SchemaSmith.MissingTableAndColumnQuench, whose
        //      "Add New Physical Columns" step (MissingTableAndColumnQuench.sql:74-89) commits the
        //      new sparse column via its own ALTER TABLE ADD.
        //   2. SchemaSmith.TableQuench.sql:25 EXECs SchemaSmith.ModifiedTableQuench -- a SEPARATE
        //      statement, running AFTER step 1 has already committed. Its drop+recreate for the
        //      converted column (ModifiedTableQuench.sql:992-994 drop, :1089-1095 re-add) issues a
        //      SECOND, independent ALTER TABLE ADD, by which point the table already has the sparse
        //      column step 1 just added.
        // SQL Server rejects a column set added to a table that already contains a sparse column
        // (Microsoft docs), so this is a genuine limitation of the two-phase design, not a bug in
        // the column-set feature itself. Reordering or merging the phases to serve this narrow case
        // was explicitly rejected as too large/risky a change -- the limitation ships instead, with
        // the engine's own clear rejection as the result (same shape as the pre-existing-sparse-
        // column case below).
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColSetConvertReject_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE TABLE dbo.{tableName} (Id INT NOT NULL, PlainXml XML NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[PlainXml]", "DataType": "XML", "Nullable": true, "IsColumnSet": true},
        {"Name": "[SparseA]", "DataType": "VARCHAR(20)", "Nullable": true, "Sparse": true}
    ]
}
""";

        try
        {
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json));
            var lowerMessage = ex!.Message.ToLowerInvariant();
            Assert.That(lowerMessage.Contains("sparse") || lowerMessage.Contains("column set"), Is.True,
                $"expected SQL Server's own column-set/sparse rejection, got: {ex.Message}");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    [Test]
    public void ShouldSurfaceEngineRejectionWhenConvertingToColumnSetOnTableWithExistingSparseColumns()
    {
        // The illegal counterpart: the table already has a standalone sparse column from a prior deploy,
        // so converting another column into the column set cannot land in the same ALTER TABLE ADD as
        // that pre-existing sparse column. SQL Server rejects this by design (Microsoft docs: "A column
        // set cannot be added to a table if that table already contains sparse columns"). No pre-
        // validation catches this ahead of time -- the engine's own rejection is the deliberate design
        // choice (Task C1-6 Step 4: prefer the engine's own error over pre-validating the relationship).
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColSetIllegal_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE TABLE dbo.{tableName} (Id INT NOT NULL, ExistingSparse VARCHAR(20) SPARSE NULL, PlainXml XML NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[ExistingSparse]", "DataType": "VARCHAR(20)", "Nullable": true, "Sparse": true},
        {"Name": "[PlainXml]", "DataType": "XML", "Nullable": true, "IsColumnSet": true}
    ]
}
""";

        try
        {
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json));
            var lowerMessage = ex!.Message.ToLowerInvariant();
            Assert.That(lowerMessage.Contains("sparse") || lowerMessage.Contains("column set"), Is.True,
                $"expected SQL Server's own column-set/sparse rejection, got: {ex.Message}");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}];";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }
}
