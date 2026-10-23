// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Parity coverage for the recyclebin hooks on MySQL: ModifiedTableQuench must route a removed
/// table through a user-installed SchemaSmith_CustomTableDrop procedure when present (instead of a
/// plain DROP TABLE), and MissingTableAndColumnQuench must call SchemaSmith_CustomTableRestore for a
/// table being added and then NOT recreate it if the restore brought it back. These hooks already
/// existed on SQL Server and PostgreSQL; MySQL was missing them.
///
/// The hooks are detected by procedure existence per database, so each test runs against its own
/// throwaway database (created, kindled, and dropped here) to stay isolated from the shared suite.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_CustomTableHookTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string BaseConnectionString { get; }

    private const string Product = "Custom Hook Tests";

    [Test]
    public void ModifiedTableQuench_RoutesRemovedTableThroughCustomTableDropHook()
    {
        var db = UniqueDb("hookdrop");
        using var conn = OpenServerConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateAndKindle(conn, cmd, db);

            // Install a CustomTableDrop hook that records the call, then drops the table.
            Exec(cmd, $"CREATE TABLE `{db}`.`HookLog` (Action VARCHAR(20), ObjName VARCHAR(128))");
            Exec(cmd, $@"
CREATE PROCEDURE `{db}`.`SchemaSmith_CustomTableDrop`(IN p_DatabaseName VARCHAR(128), IN p_TableName VARCHAR(128))
BEGIN
    INSERT INTO HookLog (Action, ObjName) VALUES ('DROP', p_TableName);
    SET @s = CONCAT('DROP TABLE `', p_DatabaseName, '`.`', p_TableName, '`');
    PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
END");

            // Create + own a table, then remove it from the product with autodrop on.
            Quench(cmd, db, "[{\"Name\":\"Gadget\",\"Columns\":[{\"Name\":\"`Id`\",\"DataType\":\"int\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PRIMARY\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"`Id`\"}]}]", false);
            Quench(cmd, db, "[{\"Name\":\"Keeper\",\"Columns\":[{\"Name\":\"`Id`\",\"DataType\":\"int\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PRIMARY\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"`Id`\"}]}]", true);

            var messages = StatusMessages(cmd);
            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, $"SELECT COUNT(*) FROM `{db}`.`HookLog` WHERE Action='DROP' AND ObjName='Gadget'"), Is.EqualTo(1),
                    "CustomTableDrop hook should have been invoked for the removed table.");
                Assert.That(TableExists(cmd, db, "Gadget"), Is.False, "Removed table should be gone (hook dropped it).");
                Assert.That(messages, Has.Some.StartsWith("  Handing table ").And.Contains("Gadget").And.Contains("CustomTableDrop"),
                    "a hooked drop says the hook has the table: " + string.Join(" | ", messages));
                Assert.That(messages, Has.None.Contains("Dropping table").And.None.Contains("Drop table:"), "it is not logged as a drop");
            });
        }
        finally
        {
            DropDb(cmd, db);
        }
        conn.Close();
    }

    [Test]
    public void MissingTableAndColumnQuench_RestoresThroughCustomTableRestoreHookAndDoesNotRecreate()
    {
        var db = UniqueDb("hookrestore");
        using var conn = OpenServerConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateAndKindle(conn, cmd, db);

            Exec(cmd, $"CREATE TABLE `{db}`.`HookLog` (Action VARCHAR(20), ObjName VARCHAR(128))");
            // A restore hook that "brings back" the table (with the product's columns) plus a sentinel
            // row, so a successful quench that keeps the row proves the hook ran AND the engine did not
            // recreate the table empty.
            Exec(cmd, $@"
CREATE PROCEDURE `{db}`.`SchemaSmith_CustomTableRestore`(IN p_DatabaseName VARCHAR(128), IN p_TableName VARCHAR(128))
BEGIN
    INSERT INTO HookLog (Action, ObjName) VALUES ('RESTORE', p_TableName);
    IF p_TableName = 'Widget' THEN
        SET @s = CONCAT('CREATE TABLE `', p_DatabaseName, '`.`Widget` (Id int NOT NULL, PRIMARY KEY (Id))');
        PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
        INSERT INTO `Widget` (Id) VALUES (42);
    END IF;
END");

            // Quench a product that defines Widget. It is missing, so the restore hook fires and
            // brings it back; the engine must then NOT recreate it (which would lose the sentinel row
            // or error on an existing table).
            Quench(cmd, db, "[{\"Name\":\"Widget\",\"Columns\":[{\"Name\":\"`Id`\",\"DataType\":\"int\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PRIMARY\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"`Id`\"}]}]", false);

            var messages = StatusMessages(cmd);
            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, $"SELECT COUNT(*) FROM `{db}`.`HookLog` WHERE Action='RESTORE' AND ObjName='Widget'"), Is.EqualTo(1),
                    "CustomTableRestore hook should have been invoked for the added table.");
                Assert.That(TableExists(cmd, db, "Widget"), Is.True, "Restored table should exist.");
                Assert.That(ScalarInt(cmd, $"SELECT COUNT(*) FROM `{db}`.`Widget` WHERE Id = 42"), Is.EqualTo(1),
                    "Restored sentinel row must survive — the engine must not recreate the table empty.");
                Assert.That(messages, Has.Some.EndsWith(" Restored").And.Contains("Widget"), "the restore's outcome is logged: " + string.Join(" | ", messages));
            });
        }
        finally
        {
            DropDb(cmd, db);
        }
        conn.Close();
    }

    // Two-deploy declarative rename against a recyclebin target: v1 creates + owns the table under
    // its old name; v2 renames it via OldName WITH autodrop on. The removed-drop pass must NOT route
    // the renamed-away old name through CustomTableDrop (which would fail on the now-missing table).
    [Test]
    public void RenamedTableIsNotRoutedThroughCustomTableDropHook()
    {
        var db = UniqueDb("hookrename");
        using var conn = OpenServerConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateAndKindle(conn, cmd, db);

            Exec(cmd, $"CREATE TABLE `{db}`.`HookLog` (Action VARCHAR(20), ObjName VARCHAR(128))");
            Exec(cmd, $@"
CREATE PROCEDURE `{db}`.`SchemaSmith_CustomTableDrop`(IN p_DatabaseName VARCHAR(128), IN p_TableName VARCHAR(128))
BEGIN
    INSERT INTO HookLog (Action, ObjName) VALUES ('DROP', p_TableName);
    SET @s = CONCAT('DROP TABLE `', p_DatabaseName, '`.`', p_TableName, '`');
    PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
END");

            // v1: create + own OrderHeader, then add data.
            Quench(cmd, db, "[{\"Name\":\"OrderHeader\",\"Columns\":[{\"Name\":\"`Id`\",\"DataType\":\"int\",\"Nullable\":false},{\"Name\":\"`Val`\",\"DataType\":\"int\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PRIMARY\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"`Id`\"}]}]", false);
            Exec(cmd, $"INSERT INTO `{db}`.`OrderHeader` (Id, Val) VALUES (1, 42)");

            // v2: rename OrderHeader -> SalesOrder via OldName, autodrop on.
            Quench(cmd, db, "[{\"Name\":\"SalesOrder\",\"OldName\":\"OrderHeader\",\"Columns\":[{\"Name\":\"`Id`\",\"DataType\":\"int\",\"Nullable\":false},{\"Name\":\"`Val`\",\"DataType\":\"int\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PRIMARY\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"`Id`\"}]}]", true);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarInt(cmd, $"SELECT COUNT(*) FROM `{db}`.`HookLog` WHERE ObjName='OrderHeader'"), Is.EqualTo(0),
                    "CustomTableDrop must NOT be invoked for a table that was renamed away.");
                Assert.That(TableExists(cmd, db, "SalesOrder"), Is.True, "Renamed table should exist.");
                Assert.That(TableExists(cmd, db, "OrderHeader"), Is.False, "Old table name should be gone.");
                Assert.That(ScalarInt(cmd, $"SELECT COUNT(*) FROM `{db}`.`SalesOrder` WHERE Id = 1 AND Val = 42"), Is.EqualTo(1),
                    "Data must be preserved across the rename.");
            });
        }
        finally
        {
            DropDb(cmd, db);
        }
        conn.Close();
    }

    private IDbConnection OpenServerConnection()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(BaseConnectionString);
        conn.Open();
        return conn;
    }

    private void CreateAndKindle(IDbConnection conn, IDbCommand cmd, string db)
    {
        Exec(cmd, $"CREATE DATABASE `{db}`");
        conn.ChangeDatabase(db);
        ForgeKindler.KindleTheForge(cmd, Platform);
    }

    private void Quench(IDbCommand cmd, string db, string json, bool autoDrop)
    {
        cmd.CommandText = $"CALL SchemaSmith_TableQuench('{Product}', '{db}', '{json.Replace("'", "''")}', 0, 0, {(autoDrop ? 1 : 0)})";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // The procedure's log lines, which SchemaQuench reads from this table while it runs.
    private static List<string> StatusMessages(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT Message FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        var messages = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) messages.Add(reader.GetString(0));
        return messages;
    }

    private static int ScalarInt(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool TableExists(IDbCommand cmd, string db, string table) =>
        ScalarInt(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='{db}' AND TABLE_NAME='{table}'") > 0;

    private static void DropDb(IDbCommand cmd, string db)
    {
        try { Exec(cmd, $"DROP DATABASE IF EXISTS `{db}`"); }
        catch { /* best-effort cleanup */ }
    }

    private static string UniqueDb(string prefix) => $"sshook_{prefix}_{Guid.NewGuid():N}".Substring(0, 40);
}
