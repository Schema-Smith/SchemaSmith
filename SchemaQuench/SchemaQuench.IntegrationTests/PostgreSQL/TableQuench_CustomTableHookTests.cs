// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Parity coverage for the recyclebin hooks on PostgreSQL: ModifiedTableQuench routes a removed table
// through a user-installed SchemaSmith.CustomTableDrop procedure when present, and
// MissingTableAndColumnQuench calls SchemaSmith.CustomTableRestore for a table being added and then
// does not recreate it if the restore brought it back. The hooks are detected per database, so each
// test runs against its own throwaway database (created, kindled, dropped here).
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CustomTableHookTests : BaseTableQuenchTests
{
    private const string Product = "Custom Hook Tests";

    [Test]
    public void ModifiedTableQuench_RoutesRemovedTableThroughCustomTableDropHook()
    {
        var db = UniqueDb("hookdrop");
        CreateDb(db);
        try
        {
            using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(DbConnString(db));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

            Exec(cmd, "CREATE TABLE public.\"HookLog\" (\"Action\" TEXT, \"ObjName\" TEXT)");
            Exec(cmd, @"
CREATE OR REPLACE PROCEDURE ""SchemaSmith"".""CustomTableDrop""(p_Schema TEXT, p_Table TEXT) LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO public.""HookLog""(""Action"", ""ObjName"") VALUES ('DROP', p_Table);
  EXECUTE 'DROP TABLE ""' || p_Schema || '"".""' || p_Table || '""';
END $$");

            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"Gadget\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_Gadget\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", false);
            var messages = new List<string>();
            conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"Keeper\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_Keeper\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", true);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"HookLog\" WHERE \"Action\"='DROP' AND \"ObjName\"='Gadget'"), Is.EqualTo(1),
                    "CustomTableDrop hook should have been invoked for the removed table.");
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"Gadget\"') IS NULL"), Is.True,
                    "Removed table should be gone (hook dropped it).");
                Assert.That(messages, Has.Some.StartsWith("  Handing table ").And.Contains("Gadget").And.Contains("CustomTableDrop"),
                    "a hooked drop says the hook has the table: " + string.Join(" | ", messages));
                Assert.That(messages, Has.None.Contains("Dropping table"), "it is not logged as a drop");
                Assert.That(messages, Has.None.Contains("Drop table:"), "it is not logged as a drop");
            });
            conn.Close();
        }
        finally
        {
            DropDb(db);
        }
    }

    [Test]
    public void MissingTableAndColumnQuench_RestoresThroughCustomTableRestoreHookAndDoesNotRecreate()
    {
        var db = UniqueDb("hookrestore");
        CreateDb(db);
        try
        {
            using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(DbConnString(db));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

            Exec(cmd, "CREATE TABLE public.\"HookLog\" (\"Action\" TEXT, \"ObjName\" TEXT)");
            Exec(cmd, @"
CREATE OR REPLACE PROCEDURE ""SchemaSmith"".""CustomTableRestore""(p_Schema TEXT, p_Table TEXT) LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO public.""HookLog""(""Action"", ""ObjName"") VALUES ('RESTORE', p_Table);
  IF p_Table = 'Widget' THEN
    EXECUTE 'CREATE TABLE ""' || p_Schema || '"".""Widget"" (""Id"" integer NOT NULL, CONSTRAINT ""PK_Widget"" PRIMARY KEY (""Id""))';
    EXECUTE 'INSERT INTO ""' || p_Schema || '"".""Widget"" (""Id"") VALUES (42)';
  END IF;
END $$");

            var messages = new List<string>();
            conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"Widget\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_Widget\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", false);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"HookLog\" WHERE \"Action\"='RESTORE' AND \"ObjName\"='Widget'"), Is.EqualTo(1),
                    "CustomTableRestore hook should have been invoked for the added table.");
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"Widget\"') IS NOT NULL"), Is.True,
                    "Restored table should exist.");
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"Widget\" WHERE \"Id\" = 42"), Is.EqualTo(1),
                    "Restored sentinel row must survive — the engine must not recreate the table empty.");
                Assert.That(messages, Has.Some.EndsWith(" Restored").And.Contains("Widget"), "the restore's outcome is logged: " + string.Join(" | ", messages));
            });
            conn.Close();
        }
        finally
        {
            DropDb(db);
        }
    }

    // Two-deploy declarative rename against a recyclebin target: v1 creates + owns the table under
    // its old name; v2 renames it via OldName WITH autodrop on. The removed-drop pass must NOT route
    // the renamed-away old name through CustomTableDrop (which would fail on the now-missing table) —
    // the exact capstone failure. Regression for the OldName exclusion in ModifiedTableQuench.
    [Test]
    public void ModifiedTableQuench_DoesNotRouteRenamedTableThroughCustomTableDropHook()
    {
        var db = UniqueDb("hookrename");
        CreateDb(db);
        try
        {
            using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(DbConnString(db));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

            Exec(cmd, "CREATE TABLE public.\"HookLog\" (\"Action\" TEXT, \"ObjName\" TEXT)");
            Exec(cmd, @"
CREATE OR REPLACE PROCEDURE ""SchemaSmith"".""CustomTableDrop""(p_Schema TEXT, p_Table TEXT) LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO public.""HookLog""(""Action"", ""ObjName"") VALUES ('DROP', p_Table);
  EXECUTE 'DROP TABLE ""' || p_Schema || '"".""' || p_Table || '""';
END $$");

            // v1: create + own OrderHeader, then add data.
            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"OrderHeader\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false},{\"Name\":\"Val\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_OrderHeader\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", false);
            Exec(cmd, "INSERT INTO public.\"OrderHeader\" (\"Id\", \"Val\") VALUES (1, 42)");

            // v2: rename OrderHeader -> SalesOrder via OldName, WITH autodrop on (exercises the removed-drop pass).
            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"SalesOrder\",\"OldName\":\"OrderHeader\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false},{\"Name\":\"Val\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_SalesOrder\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", true);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"HookLog\" WHERE \"ObjName\"='OrderHeader'"), Is.EqualTo(0),
                    "CustomTableDrop must NOT be invoked for a table that was renamed away.");
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"SalesOrder\"') IS NOT NULL"), Is.True, "Renamed table should exist.");
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"OrderHeader\"') IS NULL"), Is.True, "Old table name should be gone.");
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"SalesOrder\" WHERE \"Id\" = 1 AND \"Val\" = 42"), Is.EqualTo(1),
                    "Data must be preserved across the rename.");
            });
            conn.Close();
        }
        finally
        {
            DropDb(db);
        }
    }

    // The recyclebin registry self-eat (roadmap #1 / C3 M5 capstone): a CustomTableDrop hook soft-drops a
    // removed table by renaming it aside and recording it in a registry table. When a later package stops
    // declaring the registry itself, the removed-drop pass must NOT route the registry through the hook —
    // which would rename the registry away and then fail to INSERT into it (42P01). The registry is marked
    // PreventDrop (sticky in ownership), so the pass skips it while still recycling other removed tables.
    [Test]
    public void ModifiedTableQuench_DoesNotRoutePreventDropTableThroughCustomTableDropHook()
    {
        var db = UniqueDb("hookprotect");
        CreateDb(db);
        try
        {
            using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(DbConnString(db));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

            // Hook mirrors the recyclebin: rename the removed table aside, then record it in the Ledger. If
            // it were ever called on the Ledger itself it would rename the Ledger away and the INSERT would
            // throw — so a regression here fails the whole quench loudly, not just an assertion.
            Exec(cmd, @"
CREATE OR REPLACE PROCEDURE ""SchemaSmith"".""CustomTableDrop""(p_Schema TEXT, p_Table TEXT) LANGUAGE plpgsql AS $$
BEGIN
  EXECUTE 'ALTER TABLE ""' || p_Schema || '"".""' || p_Table || '"" RENAME TO ""' || p_Table || '_recycled""';
  INSERT INTO public.""Ledger""(""Recycled"") VALUES (p_Table);
END $$");

            // v1: own a PreventDrop Ledger (the registry) plus an ordinary Gadget.
            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"Ledger\",\"PreventDrop\":true,\"Columns\":[{\"Name\":\"Recycled\",\"DataType\":\"text\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_Ledger\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Recycled\"}]},{\"Schema\":\"public\",\"Name\":\"Gadget\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_Gadget\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", false);

            // v2: package drops both Ledger and Gadget (declares only Keeper), autodrop on. Gadget must route
            // through the hook; Ledger must be shielded by its sticky PreventDrop marker.
            Quench(cmd, "[{\"Schema\":\"public\",\"Name\":\"Keeper\",\"Columns\":[{\"Name\":\"Id\",\"DataType\":\"integer\",\"Nullable\":false}],\"Indexes\":[{\"Name\":\"PK_Keeper\",\"PrimaryKey\":true,\"Unique\":true,\"IndexColumns\":\"Id\"}]}]", true);

            Assert.Multiple(() =>
            {
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"Ledger\"') IS NOT NULL"), Is.True,
                    "PreventDrop Ledger (the registry) must survive — it must never be routed to the hook.");
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"Ledger_recycled\"') IS NULL"), Is.True,
                    "The hook must never have renamed the Ledger aside.");
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"Ledger\" WHERE \"Recycled\"='Ledger'"), Is.EqualTo(0),
                    "The hook must not have recorded the Ledger against itself.");
                Assert.That(ScalarLong(cmd, "SELECT COUNT(*) FROM public.\"Ledger\" WHERE \"Recycled\"='Gadget'"), Is.EqualTo(1),
                    "The hook must still run for the ordinary removed table and write to the surviving Ledger.");
                Assert.That(ScalarBool(cmd, "SELECT to_regclass('public.\"Gadget\"') IS NULL"), Is.True,
                    "Ordinary removed table should be recycled away by the hook.");
            });
            conn.Close();
        }
        finally
        {
            DropDb(db);
        }
    }

    private void Quench(IDbCommand cmd, string json, bool autoDrop)
    {
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{Product}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := false, p_DropTablesRemovedFromProduct := {(autoDrop ? "true" : "false")}, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool ScalarBool(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return (bool)cmd.ExecuteScalar()!;
    }

    private void CreateDb(string db)
    {
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{db}\"";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropDb(string db)
    {
        try
        {
            using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{db}\";";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch { /* best-effort cleanup */ }
    }

    private static string DbConnString(string db)
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        return ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], db, config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    private static string UniqueDb(string prefix) => $"sshook_{prefix}_{Guid.NewGuid():N}".Substring(0, 40);
}
