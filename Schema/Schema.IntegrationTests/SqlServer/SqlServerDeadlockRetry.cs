// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Threading;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Runs one statement, retrying when it is chosen as a deadlock victim. For setup that writes server-wide metadata,
/// such as enabling CDC (which creates SQL Agent jobs in msdb), and so deadlocks with the same work in another test
/// process. Matches the deadlock text rather than error 1205: a CDC enable reports it wrapped in 22832. Pass one
/// statement only, since a deadlock rolls back just the failing statement and a retried batch would re-run the rest.
/// After the retries it runs once unguarded, so a genuine failure surfaces with its own error.
/// </summary>
public static class SqlServerDeadlockRetry
{
    public static void Execute(IDbCommand cmd, string sql)
    {
        const int attempts = 5;
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
            }
            catch (DbException e) when (e.Message.Contains("deadlock victim", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
