// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace SchemaQuench;

/// <summary>
/// OnlineApply on PostgreSQL. A CONCURRENTLY statement cannot run inside a transaction block, and every quench
/// procedure is one, so the procedures queue them (<c>SchemaSmith.DeferOnlineWork</c>) and this runs the queue on the
/// same session once the procedure has returned: each statement alone, then its follow-up. A build that fails leaves
/// its index behind, invalid, so that index is dropped and the build tried once more; a second failure is the deploy's,
/// with the invalid index dropped first so the next deploy starts clean. A build that fails without leaving an invalid
/// index (the name is taken by a valid index on another table) fails at once, and that index is left alone.
/// </summary>
internal static class PostgreSqlOnlineWork
{
    internal sealed record Item(string Description, string Statement, string CleanupIndex, string FollowUp);

    private const string QueueTable = "pg_temp.temp_deferred_online_work";

    internal static void Run(IDbCommand command, Action<string> log, Action<string> logStatement)
    {
        command.Parameters.Clear();
        var items = Read(command);
        foreach (var item in items)
            RunOne(command, item, log, logStatement);
        if (items.Count > 0)
            Execute(command, $"DROP TABLE IF EXISTS {QueueTable}");
    }

    internal static List<Item> Read(IDbCommand command)
    {
        var items = new List<Item>();
        command.CommandText = $"SELECT to_regclass('{QueueTable}') IS NOT NULL";
        if (!Convert.ToBoolean(command.ExecuteScalar()))
            return items;

        command.CommandText = $"SELECT \"Description\", \"Statement\", \"CleanupIndex\", \"FollowUp\" FROM {QueueTable} ORDER BY \"Seq\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(new Item(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return items;
    }

    private static void RunOne(IDbCommand command, Item item, Action<string> log, Action<string> logStatement)
    {
        log($"    {item.Description}");
        logStatement(item.Statement);
        try
        {
            Execute(command, item.Statement);
        }
        catch (DbException) when (item.CleanupIndex != null && IsInvalidIndex(command, item.CleanupIndex))
        {
            // Named by the line above as every other line names it, not by the quoted name the cleanup statement uses.
            log("    The build stopped and left the index invalid; dropping it and building once more");
            DropInvalidIndex(command, item.CleanupIndex);
            try
            {
                Execute(command, item.Statement);
            }
            catch (DbException)
            {
                if (IsInvalidIndex(command, item.CleanupIndex))
                    DropInvalidIndex(command, item.CleanupIndex);
                throw;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.FollowUp))
        {
            logStatement(item.FollowUp);
            Execute(command, item.FollowUp);
        }
    }

    private static bool IsInvalidIndex(IDbCommand command, string index)
    {
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM pg_index WHERE indexrelid = to_regclass('{index.Replace("'", "''")}') AND NOT indisvalid)";
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    private static void DropInvalidIndex(IDbCommand command, string index) =>
        Execute(command, $"DROP INDEX CONCURRENTLY IF EXISTS {index}");

    private static void Execute(IDbCommand command, string sql)
    {
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
