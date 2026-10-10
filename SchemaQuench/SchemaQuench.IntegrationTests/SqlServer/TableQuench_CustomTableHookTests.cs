// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Parity coverage for the recyclebin hooks on SQL Server: ModifiedTableQuench routes a removed
// table through a user-installed SchemaSmith.CustomTableDrop procedure when present, and
// MissingTableAndColumnQuench calls SchemaSmith.CustomTableRestore for a table being added and then
// does not recreate it if the restore brought it back. The hooks are detected per database, so each
// test runs against its own throwaway database (created, kindled, dropped here).
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
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
        cmd.ExecuteNonQuery();
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
            // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            Exec(cmd, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IF EXISTS [{db}];");
        }
        catch { /* best-effort cleanup */ }
    }

    private static string UniqueDb(string prefix) => $"SSHook_{prefix}_{Guid.NewGuid():N}".Substring(0, 40);
}
