// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System.Text.RegularExpressions;
using Npgsql;

namespace Schema.DataAccess;

/// <summary>
/// Runs one statement of a script on the command's connection.
/// </summary>
public static class ScriptStatement
{
    private static readonly Regex BeginAtomic = new(@"\bBEGIN\s+ATOMIC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, System.TimeSpan.FromSeconds(1));

    public static void Execute(IDbCommand command, string statement)
    {
        // Npgsql splits a command's text at its semicolons to send each statement on its own, and it does not know a
        // SQL-standard routine body (BEGIN ATOMIC ... END, PostgreSQL 14), so the routine arrived in pieces and failed with
        // "syntax error at end of input". A batch command is sent as written; PostgreSqlStatementSplitter has already made
        // the statement one statement.
        if (command is NpgsqlCommand npgsql && BeginAtomic.IsMatch(statement))
        {
            using var batch = new NpgsqlBatch(npgsql.Connection, npgsql.Transaction) { Timeout = npgsql.CommandTimeout };
            batch.BatchCommands.Add(new NpgsqlBatchCommand(statement));
            command.CommandText = statement;
            batch.ExecuteNonQuery();
            return;
        }
        command.CommandText = statement;
        command.ExecuteNonQuery();
    }
}
