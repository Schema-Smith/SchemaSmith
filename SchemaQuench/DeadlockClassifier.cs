// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace SchemaQuench;

/// <summary>
/// Classifies whether an exception represents a database deadlock — the engine's
/// "rerun the transaction" signal. Recognised across the three platforms by their
/// locale-independent codes (SQL Server 1205, PostgreSQL <c>40P01</c>) with a message
/// fallback that also covers MySQL ("Deadlock found …"). Walks the inner-exception chain.
///
/// <para>Used by <see cref="DatabaseQuench"/> to retry idempotent convergence procs that lose a
/// transient deadlock race under parallel iteration — defense-in-depth alongside the SQL-level
/// prevention in the PostgreSQL convergence procs.</para>
/// </summary>
internal static class DeadlockClassifier
{
    public static bool IsDeadlock(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            // Locale-independent codes first.
            switch (e)
            {
                case SqlServerErrorException { Number: 1205 }:   // wrapped via InfoMessage
                case SqlException { Number: 1205 }:              // thrown directly
                    return true;
                case PostgresException pg when pg.SqlState == PostgresErrorCodes.DeadlockDetected:
                    return true;
            }

            // Message fallback — covers MySQL ("Deadlock found …") and any English-locale
            // message whose typed code we didn't match above.
            if (e.Message != null && e.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for the transient PostgreSQL relation-cache race — "could not open relation with OID N"
    /// — raised when a parallel iteration reads catalog for a relation a sibling concurrently dropped
    /// or recreated (the materialized-view schema-template fan-out). Like a deadlock it is safe to
    /// re-run: a fresh catalog snapshot on retry resolves it. Matched on the specific message (mirroring
    /// the MySQL deadlock message fallback) so unrelated internal errors sharing SQLSTATE XX000 are not
    /// retried. Walks the inner-exception chain.
    /// </summary>
    public static bool IsTransientRelationRace(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is PostgresException && e.Message != null &&
                e.Message.Contains("could not open relation", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// True for the SQL Server lock-timeout error (1222, "Lock request time out period exceeded").
    /// <para>
    /// This became reachable when the catalog reads stopped using WITH (NOLOCK): a dirty read waits for
    /// nothing, while a clean one takes schema stability locks that conflict with the schema-modification
    /// locks concurrent DDL holds — and a deploy is surrounded by concurrent DDL. SQL Server's default
    /// LOCK_TIMEOUT is infinite, so this only surfaces where a caller or a server default sets one, but
    /// where it does it is transient contention by definition and re-running is exactly the right answer:
    /// the convergence procs are idempotent, which is the same property that makes the deadlock retry
    /// safe.
    /// </para>
    /// </summary>
    public static bool IsLockTimeout(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            switch (e)
            {
                case SqlServerErrorException { Number: 1222 }:
                case SqlException { Number: 1222 }:
                    return true;
            }
        return false;
    }

    /// <summary>
    /// True for any transient contention an idempotent convergence proc can recover from by re-running:
    /// a deadlock (all engines), a SQL Server lock timeout, or the PostgreSQL relation-cache race under
    /// parallel fan-out.
    /// </summary>
    public static bool IsRetryableContention(Exception ex) =>
        IsDeadlock(ex) || IsLockTimeout(ex) || IsTransientRelationRace(ex);
}
