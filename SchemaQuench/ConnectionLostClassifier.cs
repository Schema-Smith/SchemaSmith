// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace SchemaQuench;

/// <summary>
/// Classifies whether an exception means the target server went away mid-deploy — restarted,
/// crashed, or was OOM-killed — as opposed to a schema/script error or an initial connect
/// failure (the latter is caught up-front by <see cref="ProductQuench"/>'s connection test).
/// Recognised across the three engines by transport/shutdown signals, with an inner
/// <see cref="SocketException"/>/<see cref="IOException"/> fallback — the literal
/// "SocketException: Success" surface seen when SQL Server is torn down mid-command
/// (SchemaSmith#353). Walks the inner-exception chain.
///
/// <para>Invoked at deploy-operation catch sites — the quench work loop, the product-script
/// path, and a top-level deploy net — where a transport <see cref="IOException"/> in the chain
/// is the overwhelmingly likely cause. The top-level net is a last resort: it still logs the raw
/// exception, so at worst a non-transport IOException there gets a slightly off headline.</para>
///
/// <para>Unlike <see cref="DeadlockClassifier"/> (which matches both <c>SqlServerErrorException</c>
/// and <c>SqlException</c> for 1205), there is no <c>SqlServerErrorException</c> branch here: a
/// connection that has actually gone away cannot deliver the <c>InfoMessage</c> that produces a
/// <c>SqlServerErrorException</c>, so a connection loss only ever arrives as a raw
/// <see cref="SqlException"/> or an inner <see cref="SocketException"/>.</para>
/// </summary>
internal static class ConnectionLostClassifier
{
    /// <summary>SQL Server error numbers meaning the connection/transport dropped (NOT -2 timeout, NOT schema errors).</summary>
    public static bool IsSqlServerConnectionLostNumber(int number) =>
        number is 233 or 64 or 20 or 10053 or 10054;

    /// <summary>MySQL error codes meaning the server is shutting down mid-run or can no longer be reached.
    /// (A "lost connection during query" drop surfaces instead as an inner <see cref="IOException"/>.)</summary>
    public static bool IsMySqlConnectionLostCode(MySqlErrorCode code) =>
        code is MySqlErrorCode.ServerShutdown or MySqlErrorCode.UnableToConnectToHost;

    /// <summary>PostgreSQL SQLSTATEs meaning the connection dropped: connection-exception class (08*)
    /// plus admin (57P01) / crash (57P02) shutdown. String literals avoid depending on Npgsql constant names.</summary>
    public static bool IsPostgresConnectionLostState(string sqlState) =>
        sqlState != null &&
        (sqlState.StartsWith("08", StringComparison.Ordinal) || sqlState == "57P01" || sqlState == "57P02");

    // The deploy also writes local files (checkpoints, logs, debug scripts), and those fail as IOException too. A
    // missing folder or a full disk on this machine is not the server going away.
    private static bool IsLocalFileError(IOException io) =>
        io is DirectoryNotFoundException or FileNotFoundException or PathTooLongException or DriveNotFoundException
        || ((io.HResult & 0xFFFF0000) == 0x80070000 && (io.HResult & 0xFFFF) is 0x20 or 0x21 or 0x27 or 0x70);

    public static bool IsConnectionLost(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                // Unambiguous transport failure anywhere in the chain. IOException also covers
                // EndOfStreamException (derived) — how Npgsql/MySqlConnector surface a torn stream.
                case SocketException:
                    return true;
                case IOException io when !IsLocalFileError(io):
                    return true;

                case SqlException sql when IsSqlServerConnectionLostNumber(sql.Number):
                    return true;

                case PostgresException pg when IsPostgresConnectionLostState(pg.SqlState):
                    return true;

                case MySqlException my when IsMySqlConnectionLostCode(my.ErrorCode):
                    return true;
            }

            // Narrow message fallback: SQL Server tears the session down before the socket error
            // surfaces ("Cannot continue the execution because the session is in the kill state.").
            // Scoped tightly so ordinary errors are not misclassified as drops.
            if (e.Message != null &&
                e.Message.Contains("the session is in the kill state", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
