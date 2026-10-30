// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.SqlClient;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Drops a test database in a way that survives what has made test drops fail and leak databases:
/// <list type="bullet">
/// <item>an idle pooled connection to it, which makes ROLLBACK IMMEDIATE wait ~3s to kill the session;</item>
/// <item>a CDC capture job, which SQL Agent reconnects into the single-user slot, leaving the database stuck in
/// SINGLE_USER and undroppable -- so CDC is disabled first, which removes its jobs;</item>
/// <item>a deadlock with another process's database DDL, which is retried.</item>
/// </list>
/// </summary>
public static class TestDatabaseDrop
{

    /// <summary>
    /// A per-fixture test database is named <c>&lt;Prefix&gt;_&lt;hex&gt;</c> from a Guid. Requiring a hex letter keeps a
    /// date-suffixed name such as <c>Archive_20240101</c> out.
    /// </summary>
    private static readonly Regex GeneratedName = new(@"^[A-Za-z][A-Za-z0-9]*_(?=[0-9a-f]*[a-f])[0-9a-f]{8,32}$", RegexOptions.Compiled);

    internal static bool IsGeneratedTestDatabaseName(string database) => GeneratedName.IsMatch(database);

    /// <summary>
    /// Drops the per-fixture databases an earlier run leaked: a fixture's teardown does not run when its process is
    /// killed, and a leaked CDC database keeps its capture jobs polling the server. Only a database the server says
    /// is more than three hours old is touched: a fixture's database lives as long as the fixture, minutes, so a
    /// database that old belongs to no run still going. The drop forces single-user, so one that is somehow in use
    /// loses its sessions.
    /// </summary>
    public static void DropStaleGeneratedDatabases(IDbCommand master)
    {
        master.CommandText = "SELECT [name] FROM sys.databases WHERE database_id > 4 AND create_date < DATEADD(HOUR, -3, GETDATE())";
        var stale = new List<string>();
        using (var reader = master.ExecuteReader())
            while (reader.Read())
                if (IsGeneratedTestDatabaseName(reader.GetString(0))) stale.Add(reader.GetString(0));

        foreach (var database in stale)
        {
            try { Drop(master, database); }
            catch (SqlException) { /* in use by a live run; leave it */ }
        }
    }
    public static void Drop(IDbCommand master, string database)
    {
        SqlConnection.ClearAllPools();
        // A drop waits for sessions to be killed and, on a busy server, can take well past the 30-second default; callers
        // often pass a fresh command that still has it.
        if (master.CommandTimeout is > 0 and < 300) master.CommandTimeout = 300;
        var name = database.Replace("]", "]]");
        var literal = database.Replace("'", "''");
        var sql = $@"
IF DB_ID(N'{literal}') IS NOT NULL
BEGIN
    -- Best effort: it only frees the slot a capture job would take. Without SQL Agent there is no job, and the disable
    -- itself can fail (SQL Server 2025 in CI: 22831 / 22912, sp_repldone failed); the drop below works either way.
    IF EXISTS (SELECT 1 FROM sys.databases WHERE [name] = N'{literal}' AND is_cdc_enabled = 1)
    BEGIN
        BEGIN TRY
            EXEC [{name}].sys.sp_cdc_disable_db;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK;
        END CATCH;
    END
    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{name}];
END";
        const int attempts = 5;
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                master.CommandText = sql;
                master.ExecuteNonQuery();
                return;
            }
            catch (SqlException e) when (e.Number == 1205)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }
        master.CommandText = sql;
        master.ExecuteNonQuery();
    }
}
