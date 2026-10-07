// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace Schema.DataAccess;

/// <summary>
/// Session settings SchemaSmith pins on a MySQL or MariaDB connection while it does its own work, restoring the server's
/// values afterwards so user scripts on the same connection run as the server is configured.
/// </summary>
public static class MySqlSessionSettings
{
    // Modes that change how SQL text is parsed or how SHOW CREATE renders it. The kindled procedures and the generated
    // delivery scripts are written for the default reading, and a stored routine keeps the mode it was created under.
    // Strictness flags (STRICT_*, NO_ZERO_DATE, ONLY_FULL_GROUP_BY) are the server's to choose and are left alone.
    internal static readonly HashSet<string> ParseAlteringModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANSI_QUOTES", "PIPES_AS_CONCAT", "NO_BACKSLASH_ESCAPES", "IGNORE_SPACE",
        "ANSI", "ORACLE", "MSSQL", "DB2", "POSTGRESQL", "MAXDB",
        "NO_KEY_OPTIONS", "NO_TABLE_OPTIONS", "NO_FIELD_OPTIONS",
        "EMPTY_STRING_IS_NULL", "SIMULTANEOUS_ASSIGNMENT"
    };

    /// <summary>The mode with every parse-altering flag removed, plus <paramref name="add"/>, in a stable order.</summary>
    public static string ParseNeutral(string sqlMode, params string[] add)
    {
        var kept = (sqlMode ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => !ParseAlteringModes.Contains(m))
            .ToList();
        foreach (var flag in add)
            if (!kept.Contains(flag, StringComparer.OrdinalIgnoreCase))
                kept.Add(flag);
        return string.Join(",", kept);
    }

    /// <summary>
    /// Switches the session to <see cref="ParseNeutral"/> of its current mode (plus <paramref name="add"/>) until the
    /// returned scope is disposed. The scope's <see cref="SqlModeScope.Mode"/> is the mode in force inside it.
    /// </summary>
    public static SqlModeScope UseParseNeutralSqlMode(IDbCommand command, params string[] add)
    {
        command.CommandText = "SELECT @@SESSION.sql_mode";
        var original = command.ExecuteScalar()?.ToString() ?? "";
        var neutral = ParseNeutral(original, add);
        if (neutral != original)
            SetSqlMode(command, neutral);
        return new SqlModeScope(command, original, neutral);
    }

    private static void SetSqlMode(IDbCommand command, string mode)
    {
        // Mode names are plain identifiers read back from the server, so they need no escaping.
        command.CommandText = $"SET SESSION sql_mode = '{mode}'";
        command.ExecuteNonQuery();
    }

    public sealed class SqlModeScope : IDisposable
    {
        private readonly IDbCommand _command;
        private readonly string _original;
        private bool _disposed;

        internal SqlModeScope(IDbCommand command, string original, string mode)
        {
            _command = command;
            _original = original;
            Mode = mode;
        }

        public string Mode { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Mode != _original && _command.Connection?.State == ConnectionState.Open)
                SetSqlMode(_command, _original);
        }
    }
}
