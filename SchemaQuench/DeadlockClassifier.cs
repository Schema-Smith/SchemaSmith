// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace SchemaQuench;

/// <summary>
/// Classifies whether an exception represents a database deadlock — the engine's
/// "rerun the transaction" signal. Recognised on every platform by its locale-independent code
/// (SQL Server 1205, PostgreSQL <c>40P01</c>, MySQL/MariaDB 1213), with an English message fallback
/// for a deadlock that reaches us through a wrapper that lost the client's exception type. Walks the
/// inner-exception chain.
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
                // MySQL/MariaDB 1213. Matched on the typed code so this engine is locale-independent
                // like the other two: MySQL translates server messages (lc_messages), so a deploy on a
                // non-English server previously depended entirely on the message fallback below --
                // which is to say, it did not classify at all.
                case MySqlException { ErrorCode: MySqlErrorCode.LockDeadlock }:
                    return true;
            }

            // Message fallback — covers any English-locale message whose typed code we didn't match
            // above (a deadlock surfaced through a wrapper that loses the client's exception type).
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
    /// True for a lock-wait timeout on any of the three engines: SQL Server 1222 ("Lock request time out
    /// period exceeded"), MySQL/MariaDB 1205 ("Lock wait timeout exceeded; try restarting transaction"),
    /// and PostgreSQL <c>55P03</c> (<c>lock_not_available</c>, raised when <c>lock_timeout</c> is set).
    /// <para>
    /// This became reachable when the catalog reads stopped using WITH (NOLOCK): a dirty read waits for
    /// nothing, while a clean one takes schema stability locks that conflict with the schema-modification
    /// locks concurrent DDL holds — and a deploy is surrounded by concurrent DDL. Re-running is exactly
    /// the right answer: the convergence procs are idempotent, which is the same property that makes the
    /// deadlock retry safe.
    /// </para>
    /// <para>
    /// <b>How often each engine can actually raise it differs enough to matter.</b> SQL Server's default
    /// LOCK_TIMEOUT is infinite and PostgreSQL's <c>lock_timeout</c> defaults to 0 (disabled), so on those
    /// two this surfaces only where a caller or server default sets one. MySQL is the opposite: InnoDB's
    /// <c>innodb_lock_wait_timeout</c> defaults to 50 seconds, so a MySQL deploy contending with
    /// concurrent DDL reaches this by default, with nothing configured. Recognising only the SQL Server
    /// number would have left the engine most likely to raise it as the one that never retried.
    /// </para>
    /// <para>
    /// MySQL's 1205 is a lock-wait timeout, NOT a deadlock — its deadlock is 1213 — so the numbers do not
    /// line up across engines and each is matched against its own client's typed code. Nothing here reads
    /// a raw integer off an untyped exception, precisely because 1205 means different things on different
    /// engines.
    /// </para>
    /// <para>
    /// Deliberately NOT included: PostgreSQL <c>57014</c> (<c>query_canceled</c>, from
    /// <c>statement_timeout</c>) and MySQL's <c>max_execution_time</c> cancellation. Those are a caller's
    /// deliberate cap on how long a statement may run, not contention — retrying one fights the intent
    /// that set it, and a statement slow enough to exceed the cap would simply exceed it again on all
    /// twenty attempts.
    /// </para>
    /// </summary>
    public static bool IsLockTimeout(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            switch (e)
            {
                case SqlServerErrorException { Number: 1222 }:   // wrapped via InfoMessage
                case SqlException { Number: 1222 }:              // thrown directly
                    return true;
                case MySqlException my when IsMySqlLockTimeoutCode(my.ErrorCode):
                    return true;
                case PostgresException pg when IsPostgresLockTimeoutState(pg.SqlState):
                    return true;
            }
        return false;
    }

    /// <summary>
    /// InnoDB's lock-wait timeout (1205). Exposed as a code predicate rather than checked inline for the
    /// same reason <see cref="ConnectionLostClassifier.IsMySqlConnectionLostCode"/> is: MySqlConnector's
    /// exception cannot be constructed from a test, so this is the only surface where the classification
    /// can be proven rather than assumed.
    /// </summary>
    public static bool IsMySqlLockTimeoutCode(MySqlErrorCode code) =>
        code == MySqlErrorCode.LockWaitTimeout;

    /// <summary>
    /// PostgreSQL <c>55P03</c> (<c>lock_not_available</c>) — what a statement raises when
    /// <c>lock_timeout</c> is set and it waits past it. A string literal avoids depending on an Npgsql
    /// constant name, matching <see cref="ConnectionLostClassifier.IsPostgresConnectionLostState"/>.
    /// </summary>
    public static bool IsPostgresLockTimeoutState(string sqlState) => sqlState == "55P03";

    /// <summary>
    /// True for the MySQL/MariaDB transient failures a parallel deploy provokes by reading catalog and
    /// churning temp tables while a sibling work unit runs DDL.
    /// <para>
    /// <b>1684</b> — "Table '…' was skipped since its definition is being modified by concurrent DDL
    /// statement" — is the MySQL equivalent of the PostgreSQL relation-cache race in
    /// <see cref="IsTransientRelationRace"/>, and arrives for the same reason: a catalog read caught an
    /// object mid-alter. PostgreSQL's form was already retried and MySQL's was not, which is the accidental
    /// single-engine gap the platform-parity rule exists to catch. The engine SKIPS the object rather than
    /// returning a bad row, so the read is incomplete rather than wrong — re-running gets a consistent one.
    /// </para>
    /// <para>
    /// <b>1467</b> — "Failed to read auto-increment value from storage engine" — is transient in the same
    /// way. The convergence procs create and drop several AUTO_INCREMENT temp tables per call, and under
    /// concurrent work units that read can fail outright; the retry recreates them.
    /// </para>
    /// <para>
    /// Both are safe for exactly the reason the deadlock retry is: nothing partial survives a failed call
    /// that a re-run would not recompute, because the convergence procs derive desired-vs-existing from
    /// scratch every time. Observed against MariaDB and MySQL under two test workers -- three failures in
    /// four full-assembly runs, every one an exception rather than a wrong answer.
    /// </para>
    /// </summary>
    public static bool IsMySqlTransientConcurrencyCode(MySqlErrorCode code) =>
        // 1684 has no named member in MySqlConnector's enum, so it is matched by value. Named constants
        // are preferred everywhere else here precisely because a bare number is easy to misread -- hence
        // the spelled-out name beside it.
        code == (MySqlErrorCode)1684                        // ER_WARN_I_S_SKIPPED_TABLE
        || code == MySqlErrorCode.AutoIncrementReadFailed;  // 1467

    /// <summary>
    /// True for any transient contention an idempotent convergence proc can recover from by re-running:
    /// a deadlock (all engines), a lock-wait timeout (all engines), the PostgreSQL relation-cache race, or
    /// its MySQL/MariaDB counterparts under parallel fan-out.
    /// </summary>
    public static bool IsRetryableContention(Exception ex) =>
        IsDeadlock(ex) || IsLockTimeout(ex) || IsTransientRelationRace(ex)
        || IsMySqlTransientConcurrency(ex);

    private static bool IsMySqlTransientConcurrency(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is MySqlException my && IsMySqlTransientConcurrencyCode(my.ErrorCode))
                return true;
        return false;
    }
}
