// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;

namespace SchemaQuench;

/// <summary>
/// H1, tier 2: a drift check on MySQL or MariaDB with kindling on keeps the SchemaSmith helpers in a database of their own,
/// so the target is left untouched. The connection stays on the target -- user SQL such as a folder gate still reads it as
/// the current database -- and every SchemaSmith routine and table a statement names is qualified with the helper
/// database. A routine runs with its own database as the default, so what it reads unqualified is the helper's too.
/// </summary>
internal static partial class DriftHelperDatabase
{
    // A SchemaSmith routine or table (or a temp table a routine creates, _SchemaSmith_...), not already qualified and not
    // inside a string literal. Session variables are spelled in lower case (@schemasmith_...), so they never match.
    [GeneratedRegex(@"(?<![.\w`$@'])(_?SchemaSmith_[A-Za-z0-9_]+)")]
    private static partial Regex SchemaSmithName();

    public static string Qualify(string sql, string helperDatabase)
    {
        if (string.IsNullOrEmpty(sql)) return sql;
        // Only the SQL itself: a name inside a string ('...', "...") or a quoted identifier is data, such as a user table
        // named SchemaSmith_Log in the table JSON a routine is handed.
        var result = new System.Text.StringBuilder(sql.Length + 32);
        var code = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is not ('\'' or '"' or '`')) { i++; continue; }
            result.Append(SchemaSmithName().Replace(sql[code..i], m => $"{Quote(helperDatabase)}.{m.Groups[1].Value}"));
            var start = i++;
            while (i < sql.Length)
            {
                if (sql[i] == '\\' && c != '`') { i += 2; continue; }
                if (sql[i] == c)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; }
                    i++;
                    break;
                }
                i++;
            }
            i = Math.Min(i, sql.Length);
            result.Append(sql, start, i - start);
            code = i;
        }
        result.Append(SchemaSmithName().Replace(sql[code..], m => $"{Quote(helperDatabase)}.{m.Groups[1].Value}"));
        return result.ToString();
    }

    public static string Quote(string name) => $"`{name.Replace("`", "``")}`";

    /// <summary>The state a deploy reads back from the target: what it owns, what it stamped, what it ran, what it recorded.</summary>
    public static readonly IReadOnlyList<string> StateTables =
    [
        "SchemaSmith_ProductOwnership", "SchemaSmith_ScriptStamp", "SchemaSmith_CompletedMigrationScripts", "SchemaSmith_ExpressionMap",
    ];

    /// <summary>
    /// Replaces the helper's state with the target's, the columns both have: kindled into a scratch database they are empty,
    /// and every table would read as unowned, every module as changed, every migration as pending.
    /// </summary>
    public static void CopyState(IDbCommand command, string targetDatabase, string helperDatabase)
    {
        foreach (var table in StateTables)
        {
            var helperColumns = Columns(command, helperDatabase, table);
            var targetColumns = Columns(command, targetDatabase, table);
            command.CommandText = $"DELETE FROM {Quote(helperDatabase)}.{table}";
            command.ExecuteNonQuery();
            var shared = helperColumns.Where(c => targetColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).Select(Quote).ToList();
            if (shared.Count == 0) continue;
            var list = string.Join(", ", shared);
            command.CommandText = $"INSERT INTO {Quote(helperDatabase)}.{table} ({list}) SELECT {list} FROM {Quote(targetDatabase)}.{table}";
            command.ExecuteNonQuery();
        }
    }

    private static List<string> Columns(IDbCommand command, string database, string table)
    {
        command.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '"
                              + database.Replace("'", "''").Replace("\\", "\\\\") + "' AND TABLE_NAME = '" + table + "' ORDER BY ORDINAL_POSITION";
        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }
}

/// <summary>A target-connection command whose statements name the SchemaSmith helpers in the drift helper database.</summary>
internal sealed class HelperQualifyingCommand(IDbCommand inner, string helperDatabase) : IDbCommand
{
    public string CommandText { get => inner.CommandText; set => inner.CommandText = DriftHelperDatabase.Qualify(value, helperDatabase); }

    public int ExecuteNonQuery() => inner.ExecuteNonQuery();
    public object ExecuteScalar() => inner.ExecuteScalar();
    public IDataReader ExecuteReader() => inner.ExecuteReader();
    public IDataReader ExecuteReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
    public void Prepare() => inner.Prepare();
    public void Cancel() => inner.Cancel();
    public IDbDataParameter CreateParameter() => inner.CreateParameter();
    public void Dispose() => inner.Dispose();

    public int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public IDbConnection Connection { get => inner.Connection; set => inner.Connection = value; }
    public IDataParameterCollection Parameters => inner.Parameters;
    public IDbTransaction Transaction { get => inner.Transaction; set => inner.Transaction = value; }
    public UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
}
