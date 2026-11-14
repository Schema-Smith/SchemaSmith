// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Schema.Domain;

namespace SchemaQuench;

/// <summary>
/// The login the engine reports for a session, asked after connecting rather than read from the settings file: under
/// Windows authentication, Kerberos or a cloud identity the settings hold no user, and the engine still knows who the
/// session is. A name, never a credential.
/// </summary>
internal static class SessionLogin
{
    private static readonly ConcurrentDictionary<string, bool> WarnedServers = new(StringComparer.OrdinalIgnoreCase);

    internal static string Query(Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => "SELECT ORIGINAL_LOGIN()",
        Platform.PostgreSQL => "SELECT CAST(session_user AS TEXT)",
        _ => "SELECT SUBSTRING_INDEX(USER(), '@', 1)"
    };

    private static string Unread(string server, Exception ex, Action<string> warn)
    {
        if (WarnedServers.TryAdd(server ?? "", true))
            warn($"[{server}] Could not read the session's login for the deployment summary: {ex.Message}");
        return null;
    }

    internal static string Read(IDbCommand command, Platform platform, string server, Action<string> warn)
    {
        try
        {
            command.CommandText = Query(platform);
            var value = command.ExecuteScalar();
            return value == null || value is DBNull ? null : value.ToString();
        }
        catch (DbException ex)
        {
            return Unread(server, ex, warn);
        }
        catch (SqlServerErrorException ex)
        {
            return Unread(server, ex, warn);
        }
    }
}
