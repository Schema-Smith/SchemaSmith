// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
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
