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
        var name = database.Replace("]", "]]");
        var literal = database.Replace("'", "''");
        var sql = $@"
IF DB_ID(N'{literal}') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.databases WHERE [name] = N'{literal}' AND is_cdc_enabled = 1)
        EXEC [{name}].sys.sp_cdc_disable_db;
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
