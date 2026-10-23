// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Parity coverage for the recyclebin hooks on SQL Server: ModifiedTableQuench routes a removed
// table through a user-installed SchemaSmith.CustomTableDrop procedure when present, and
// MissingTableAndColumnQuench calls SchemaSmith.CustomTableRestore for a table being added and then
// does not recreate it if the restore brought it back. The hooks are detected per database, so each
// test runs against its own throwaway database (created, kindled, dropped here).
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CustomTableHookTests : BaseTableQuenchTests
{
    private const string Product = "Custom Hook Tests";

    [Test]
    public void ModifiedTableQuench_RoutesRemovedTableThroughCustomTableDropHook()
    {
        var db = UniqueDb("hookdrop");
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateAndKindle(conn, cmd, db);

            Exec(cmd, "CREATE TABLE dbo.HookLog (Action VARCHAR(20), ObjName SYSNAME)");
            Exec(cmd, @"
CREATE PROCEDURE SchemaSmith.CustomTableDrop @SchemaName SYSNAME, @TableName SYSNAME AS
BEGIN
  INSERT INTO dbo.HookLog VALUES ('DROP', @TableName);
  EXEC('DROP TABLE [' + @SchemaName + '].[' + @TableName + ']');
END");

            Quench(cmd, db, "[{\"Schema\":\"[dbo]\",\"Name\":\"[Gadget]\",\"Columns\":[{\"Name\":\"[Id]\",\"DataType\":\"INT\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"[PK_Gadget]\",\"PrimaryKey\":true,\"Unique\":true,\"Clustered\":true,\"IndexColumns\":\"[Id]\"}]}]", false);
            Quench(cmd, db, "[{\"Schema\":\"[dbo]\",\"Name\":\"[Keeper]\",\"Columns\":[{\"Name\":\"[Id]\",\"DataType\":\"INT\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"[PK_Keeper]\",\"PrimaryKey\":true,\"Unique\":true,\"Clustered\":true,\"IndexColumns\":\"[Id]\"}]}]", true);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM dbo.HookLog WHERE Action='DROP' AND ObjName='Gadget'"), Is.EqualTo(1),
                    "CustomTableDrop hook should have been invoked for the removed table.");
                Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.Gadget') IS NULL THEN 0 ELSE 1 END"), Is.EqualTo(0),
                    "Removed table should be gone (hook dropped it).");
            });
        }
        finally
        {
            DropDb(conn, cmd, db);
        }
        conn.Close();
    }

    // #549: the reference recyclebin hook moves the table with ALTER SCHEMA TRANSFER, which keeps its ProductName property,
    // so every later deploy found the recycled copy, "dropped" it again and recycled it under a new name -- the registry
    // row then pointed at nothing, and a restore could not bring the data back.
    [Test]
    public void ATableTheDropHookMoved_IsNoLongerTheProducts_SoItIsRecycledOnce()
    {
        var db = UniqueDb("hookonce");
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateAndKindle(conn, cmd, db);

            Exec(cmd, "CREATE TABLE dbo.HookLog (Action VARCHAR(20), ObjName SYSNAME)");
            Exec(cmd, "CREATE SCHEMA recyclebin");
            Exec(cmd, @"
CREATE PROCEDURE SchemaSmith.CustomTableDrop @SchemaName SYSNAME, @TableName SYSNAME AS
BEGIN
  INSERT INTO dbo.HookLog VALUES ('DROP', @TableName);
  EXEC('ALTER SCHEMA recyclebin TRANSFER [' + @SchemaName + '].[' + @TableName + ']');
END");

            const string gadget = "[{\"Schema\":\"[dbo]\",\"Name\":\"[Gadget]\",\"Columns\":[{\"Name\":\"[Id]\",\"DataType\":\"INT\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"[PK_Gadget]\",\"PrimaryKey\":true,\"Unique\":true,\"Clustered\":true,\"IndexColumns\":\"[Id]\"}]}]";
            // The recyclebin framework's own table is part of the package, as in the reference hooks, so the recyclebin
            // schema is one the deploy reads -- which is where it found the recycled copy.
            const string keeper = "[{\"Schema\":\"[dbo]\",\"Name\":\"[Keeper]\",\"Columns\":[{\"Name\":\"[Id]\",\"DataType\":\"INT\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"[PK_Keeper]\",\"PrimaryKey\":true,\"Unique\":true,\"Clustered\":true,\"IndexColumns\":\"[Id]\"}]},"
                                  + "{\"Schema\":\"[recyclebin]\",\"Name\":\"[Registry]\",\"Columns\":[{\"Name\":\"[RecycledName]\",\"DataType\":\"SYSNAME\",\"Nullable\":false}]}]";
            Quench(cmd, db, gadget, false);
            Exec(cmd, "INSERT INTO dbo.Gadget (Id) VALUES (7)");
            Quench(cmd, db, keeper, true);
            Quench(cmd, db, keeper, true);
            Quench(cmd, db, keeper, true);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM dbo.HookLog WHERE Action='DROP'"), Is.EqualTo(1),
                    "the hook recycles the table once; the recycled copy is not the product's any more");
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM recyclebin.Gadget WHERE Id = 7"), Is.EqualTo(1),
                    "the recycled copy keeps its name and its rows, so a restore can find it");
            });

            // Declared again, the table comes back through the restore hook with its rows, and is the product's again.
            Exec(cmd, @"
CREATE PROCEDURE SchemaSmith.CustomTableRestore @SchemaName SYSNAME, @TableName SYSNAME AS
BEGIN
  IF OBJECT_ID('recyclebin.' + QUOTENAME(@TableName)) IS NOT NULL
    EXEC('ALTER SCHEMA [' + @SchemaName + '] TRANSFER recyclebin.[' + @TableName + ']');
END");
            Quench(cmd, db, gadget.TrimEnd(']') + "," + keeper.TrimStart('['), true);
            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM dbo.Gadget WHERE Id = 7"), Is.EqualTo(1), "restored with its rows");
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 "
                                           + "AND major_id = OBJECT_ID('dbo.Gadget') AND name = 'ProductName'"), Is.EqualTo(1),
                    "the restored table is the product's again");
            });
        }
        finally
        {
            DropDb(conn, cmd, db);
        }
        conn.Close();
    }

    [Test]
    public void MissingTableAndColumnQuench_RestoresThroughCustomTableRestoreHookAndDoesNotRecreate()
    {
        var db = UniqueDb("hookrestore");
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateAndKindle(conn, cmd, db);

            Exec(cmd, "CREATE TABLE dbo.HookLog (Action VARCHAR(20), ObjName SYSNAME)");
            Exec(cmd, @"
CREATE PROCEDURE SchemaSmith.CustomTableRestore @SchemaName SYSNAME, @TableName SYSNAME AS
BEGIN
  INSERT INTO dbo.HookLog VALUES ('RESTORE', @TableName);
  IF @TableName = 'Widget'
  BEGIN
    CREATE TABLE dbo.Widget (Id INT NOT NULL CONSTRAINT PK_Widget PRIMARY KEY CLUSTERED (Id));
    INSERT INTO dbo.Widget (Id) VALUES (42);
  END
END");

            Quench(cmd, db, "[{\"Schema\":\"[dbo]\",\"Name\":\"[Widget]\",\"Columns\":[{\"Name\":\"[Id]\",\"DataType\":\"INT\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"[PK_Widget]\",\"PrimaryKey\":true,\"Unique\":true,\"Clustered\":true,\"IndexColumns\":\"[Id]\"}]}]", false);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM dbo.HookLog WHERE Action='RESTORE' AND ObjName='Widget'"), Is.EqualTo(1),
                    "CustomTableRestore hook should have been invoked for the added table.");
                Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.Widget') IS NULL THEN 0 ELSE 1 END"), Is.EqualTo(1),
                    "Restored table should exist.");
                Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM dbo.Widget WHERE Id = 42"), Is.EqualTo(1),
                    "Restored sentinel row must survive — the engine must not recreate the table empty.");
            });
        }
        finally
        {
            DropDb(conn, cmd, db);
        }
        conn.Close();
    }

    private void CreateAndKindle(SqlConnection conn, IDbCommand cmd, string db)
    {
        Exec(cmd, $"CREATE DATABASE [{db}]");
        conn.ChangeDatabase(db);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
    }

    private void Quench(IDbCommand cmd, string db, string json, bool autoDrop)
    {
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{Product}', @TableDefinitions = '{json.Replace("'", "''")}', @WhatIf = 0, @DropTablesRemovedFromProduct = {(autoDrop ? 1 : 0)}, @DropUnknownIndexes = 0";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int ScalarInt(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void DropDb(SqlConnection conn, IDbCommand cmd, string db)
    {
        try
        {
            conn.ChangeDatabase("master");
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
        }
        catch { /* best-effort cleanup */ }
    }

    private static string UniqueDb(string prefix) => $"SSHook_{prefix}_{Guid.NewGuid():N}".Substring(0, 40);
}
