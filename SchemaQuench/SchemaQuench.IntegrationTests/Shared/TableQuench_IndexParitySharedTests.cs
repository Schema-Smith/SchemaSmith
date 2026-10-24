// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Index-B parity coverage (#270): MySQL removed-from-product index reconciliation must fire by
/// DEFAULT (gated by DropIndexesRemovedFromProduct, NOT coupled to DropUnknownIndexes), and MySQL
/// must detect and drop genuinely out-of-band (unowned, not-in-definition) indexes when
/// DropUnknownIndexes is on — matching SQL Server / PostgreSQL on both axes.
///
/// Index removal is performed by ModifiedTableQuench, NOT MissingIndexesAndConstraintsQuench — the
/// two index-drop flags are its last two parameters:
/// (p_ProductName, p_DatabaseName, p_WhatIf, p_DropTablesRemovedFromProduct,
///  p_DropColumnsRemovedFromProduct, p_DropCheckConstraintsRemovedFromProduct,
///  p_DropExcludeConstraintsRemovedFromProduct, p_DropStatisticsRemovedFromProduct,
///  p_CaptureWouldDrop, p_DropUnknownIndexes, p_DropIndexesRemovedFromProduct)
/// IndexOnlyQuench keeps its own flags and is unchanged.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_IndexParitySharedTests : BaseTableQuenchTests
{
    [Test]
    public void RemovedFromProduct_DropsByDefault_WithUnknownIndexesOff()
    {
        var product = $"IdxParity_{Guid.NewGuid():N}";
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var table = $"idx_removed_{uniqueId}";
        var index = $"ix_{uniqueId}_val";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Phase 1: quench the table WITH its index (records ProductOwnership).
            RunTableQuenchProc(cmd, WithIndexJson(table, index), productName: product);
            Assert.That(IndexExists(cmd, table, index), Is.True, "Index must exist after phase 1.");

            // Phase 2: reconcile with the index removed, DropUnknownIndexes=0. The removed-from-product
            // drop must still fire (gated only by DropIndexesRemovedFromProduct, default on).
            RunIndexReconcileQuench(cmd, product, WithoutIndexJson(table, dropFlag: null),
                dropUnknownIndexes: 0, dropIndexesRemovedFromProduct: 1);

            Assert.That(IndexExists(cmd, table, index), Is.False,
                "Removed-from-product index must drop by default even with DropUnknownIndexes=0.");
        }
        finally
        {
            Cleanup(cmd, table, product);
        }
        conn.Close();
    }

    [Test]
    public void RemovedFromProduct_Suppressed_WhenTableFlagFalse()
    {
        var product = $"IdxParity_{Guid.NewGuid():N}";
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var table = $"idx_suppress_{uniqueId}";
        var index = $"ix_{uniqueId}_val";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithIndexJson(table, index), productName: product);

            // Table opts out via DropIndexesRemovedFromProduct:false — index must survive.
            RunIndexReconcileQuench(cmd, product, WithoutIndexJson(table, dropFlag: false),
                dropUnknownIndexes: 0, dropIndexesRemovedFromProduct: 1);

            Assert.That(IndexExists(cmd, table, index), Is.True,
                "DropIndexesRemovedFromProduct:false must suppress the removed-from-product drop.");
        }
        finally
        {
            Cleanup(cmd, table, product);
        }
        conn.Close();
    }

    [Test]
    public void OutOfBand_DroppedOnlyWithUnknownIndexesOn()
    {
        var product = $"IdxParity_{Guid.NewGuid():N}";
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var table = $"idx_oob_{uniqueId}";
        var oobIndex = $"ix_oob_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Phase 1: quench the table WITHOUT any secondary index (table is owned; no index is owned).
            RunTableQuenchProc(cmd, WithoutIndexJson(table, dropFlag: null), productName: product);

            // Create an out-of-band index directly via DDL — NOT recorded in ProductOwnership.
            cmd.CommandText = $"CREATE INDEX `{oobIndex}` ON `{_mainDb}`.`{table}` (`Val`)";
            cmd.ExecuteNonQuery();
            Assert.That(IndexExists(cmd, table, oobIndex), Is.True, "Out-of-band index must exist before reconcile.");

            // DropUnknownIndexes=0: the unowned index must be LEFT ALONE.
            RunIndexReconcileQuench(cmd, product, WithoutIndexJson(table, dropFlag: null),
                dropUnknownIndexes: 0, dropIndexesRemovedFromProduct: 1);
            Assert.That(IndexExists(cmd, table, oobIndex), Is.True,
                "Out-of-band index must NOT be dropped with DropUnknownIndexes=0.");

            // DropUnknownIndexes=1: the unowned index must be DROPPED.
            RunIndexReconcileQuench(cmd, product, WithoutIndexJson(table, dropFlag: null),
                dropUnknownIndexes: 1, dropIndexesRemovedFromProduct: 1);
            Assert.That(IndexExists(cmd, table, oobIndex), Is.False,
                "Out-of-band index must be dropped with DropUnknownIndexes=1.");
        }
        finally
        {
            Cleanup(cmd, table, product);
        }
        conn.Close();
    }

    [Test]
    public void IndexOnly_RemovedFromProduct_DropsByDefault_WithUnknownIndexesOff()
    {
        var product = $"IdxParityIO_{Guid.NewGuid():N}";
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var table = $"idxio_removed_{uniqueId}";
        var index = $"ix_{uniqueId}_val";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithIndexJson(table, index), productName: product);
            Assert.That(IndexExists(cmd, table, index), Is.True, "Index must exist after phase 1.");

            // IndexOnly reconcile with the index removed, DropUnknownIndexes=0 — must still drop.
            RunIndexOnlyQuench(cmd, product, WithoutIndexJson(table, dropFlag: null),
                dropUnknownIndexes: 0, dropIndexesRemovedFromProduct: 1);

            Assert.That(IndexExists(cmd, table, index), Is.False,
                "IndexOnly: removed-from-product index must drop by default even with DropUnknownIndexes=0.");
        }
        finally
        {
            Cleanup(cmd, table, product);
        }
        conn.Close();
    }

    [Test]
    public void IndexOnly_OutOfBand_DroppedOnlyWithUnknownIndexesOn()
    {
        var product = $"IdxParityIO_{Guid.NewGuid():N}";
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var table = $"idxio_oob_{uniqueId}";
        var oobIndex = $"ix_oob_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithoutIndexJson(table, dropFlag: null), productName: product);
            cmd.CommandText = $"CREATE INDEX `{oobIndex}` ON `{_mainDb}`.`{table}` (`Val`)";
            cmd.ExecuteNonQuery();
            Assert.That(IndexExists(cmd, table, oobIndex), Is.True, "Out-of-band index must exist before reconcile.");

            RunIndexOnlyQuench(cmd, product, WithoutIndexJson(table, dropFlag: null),
                dropUnknownIndexes: 0, dropIndexesRemovedFromProduct: 1);
            Assert.That(IndexExists(cmd, table, oobIndex), Is.True,
                "IndexOnly: out-of-band index must NOT be dropped with DropUnknownIndexes=0.");

            RunIndexOnlyQuench(cmd, product, WithoutIndexJson(table, dropFlag: null),
                dropUnknownIndexes: 1, dropIndexesRemovedFromProduct: 1);
            Assert.That(IndexExists(cmd, table, oobIndex), Is.False,
                "IndexOnly: out-of-band index must be dropped with DropUnknownIndexes=1.");
        }
        finally
        {
            Cleanup(cmd, table, product);
        }
        conn.Close();
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private static string WithIndexJson(string table, string index) => $$"""
        [
        {
            "Name": "`{{table}}`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
            "Indexes": [ { "Name": "`{{index}}`", "IndexColumns": "`Val`", "Unique": false, "PrimaryKey": false } ]
        }
        ]
        """;

    private static string WithoutIndexJson(string table, bool? dropFlag)
    {
        var flag = dropFlag.HasValue ? $"\"DropIndexesRemovedFromProduct\": {(dropFlag.Value ? "true" : "false")}," : "";
        return $$"""
            [
            {
                "Name": "`{{table}}`",
                {{flag}}
                "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
                "Indexes": []
            }
            ]
            """;
    }

    // Index removal lives in ModifiedTableQuench, so the two flags under test are its LAST two
    // arguments. The eight before them are fixed at values that keep the reconcile index-only:
    // WhatIf=0, DropTablesRemovedFromProduct=0 and DropColumnsRemovedFromProduct=0 (the table and
    // both its columns must survive every reconcile these tests run), DropCheck/Exclude/Statistics=1,
    // CaptureWouldDrop=0. MissingIndexesAndConstraintsQuench is not called: every reconcile here
    // declares an empty index list and no check constraints, so its create half has nothing to do.
    private void RunIndexReconcileQuench(System.Data.IDbCommand cmd, string product, string json,
        int dropUnknownIndexes, int dropIndexesRemovedFromProduct)
    {
        cmd.CommandText =
            $"CALL SchemaSmith_ParseTableJson('{_mainDb}', '{json.Replace("'", "''")}'); " +
            $"CALL SchemaSmith_ModifiedTableQuench('{product}', '{_mainDb}', 0, 0, 0, 1, 1, 1, 0, {dropUnknownIndexes}, {dropIndexesRemovedFromProduct});";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private void RunIndexOnlyQuench(System.Data.IDbCommand cmd, string product, string json,
        int dropUnknownIndexes, int dropIndexesRemovedFromProduct)
    {
        cmd.CommandText =
            $"CALL SchemaSmith_ParseTableJson('{_mainDb}', '{json.Replace("'", "''")}'); " +
            $"CALL SchemaSmith_IndexOnlyQuench('{product}', '{_mainDb}', 0, {dropUnknownIndexes}, {dropIndexesRemovedFromProduct});";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private void Cleanup(System.Data.IDbCommand cmd, string table, string product)
    {
        try
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = '{product}'";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
        }
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }
}
