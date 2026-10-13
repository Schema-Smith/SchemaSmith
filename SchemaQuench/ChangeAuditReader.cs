// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Schema.Domain;

namespace SchemaQuench;

/// <summary>
/// End-of-work-unit drain of the session-scoped ChangeAudit table (#243 E5). Runs on the SAME
/// connection the 4 table procs wrote on (tableConnection for SQL Server / PostgreSQL, the single
/// connection for MySQL), so the session filter is exact and uncommitted same-session rows are
/// visible. Returns null for an engine whose procs do not yet emit, or when the audit table is
/// absent (kindling suppressed) — the caller then leaves the run honestly not-instrumented. Deletes
/// its own session's rows after reading (self-cleaning, mirrors StatusMessageMonitor).
/// </summary>
public static class ChangeAuditReader
{
    public static IReadOnlyList<ChangeAuditRow> ReadAndDrain(Platform platform, IDbCommand command) =>
        SessionSql(platform) is { } sql ? ReadRows(command, sql.Select, sql.Delete) : null;

    /// <summary>
    /// Discards rows already carrying this connection's session id before the work unit writes any. They belong to an
    /// earlier session that ended without draining -- a deploy that died part-way -- and the engine has since given its
    /// id to this one; read at the end, they would be reported as this deploy's changes. Best-effort, like the drain:
    /// before kindling the table does not exist yet, and then there is nothing stale to discard.
    /// </summary>
    public static void DiscardStale(Platform platform, IDbCommand command)
    {
        if (SessionSql(platform) is not { } sql) return;
        try
        {
            command.Parameters.Clear();
            // SQL Server's table connection reports user errors as info messages, which the run logs as errors, instead
            // of throwing -- so the missing table is checked for rather than caught.
            command.CommandText = platform.GetBasePlatform() == Platform.SqlServer
                ? $"IF OBJECT_ID(N'SchemaSmith.ChangeAudit', N'U') IS NOT NULL {sql.Delete}"
                : sql.Delete;
            command.ExecuteNonQuery();
        }
        catch (DbException)
        {
            // Audit table absent (kindling not yet run, or suppressed): nothing to discard.
        }
    }

    private static (string Select, string Delete)? SessionSql(Platform platform) =>
        platform.GetBasePlatform() switch
        {
            Platform.SqlServer => (
                "SELECT ObjectType, ObjectName, ActionType FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID ORDER BY Id",
                "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID"),
            Platform.PostgreSQL => (
                "SELECT \"ObjectType\", \"ObjectName\", \"ActionType\" FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() ORDER BY \"Id\"",
                "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()"),
            Platform.MySQL => (
                "SELECT ObjectType, ObjectName, ActionType FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() ORDER BY Id",
                "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()"),
            _ => null
        };

    private static IReadOnlyList<ChangeAuditRow> ReadRows(IDbCommand command, string selectSql, string deleteSql)
    {
        try
        {
            var rows = new List<ChangeAuditRow>();
            command.Parameters.Clear();
            command.CommandText = selectSql;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    rows.Add(new ChangeAuditRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            command.CommandText = deleteSql;
            command.ExecuteNonQuery();
            return rows;
        }
        catch (DbException)
        {
            // Best-effort drain (see DatabaseQuench.DrainChangeAudit): an audit-read failure must
            // NEVER disrupt the run. Audit table absent / unreadable (kindling suppressed) — treat
            // as not instrumented.
            return null;
        }
        catch (InvalidOperationException)
        {
            // The shared table connection was reset or closed under concurrency ("Connection is not
            // open"; also covers ObjectDisposedException, its subclass) — a deadlock victim or dropped
            // connection surfaces here because the drain is the last op on that connection. Leave the
            // run honestly not-instrumented instead of masking the real quench result in the finally.
            return null;
        }
    }
}
