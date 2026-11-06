// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Npgsql;
using Schema.Domain;

namespace Schema.DataAccess;

public static class ConnectionString
{
    public static string Build(Platform platform, string serverName, string dbName, string user, string password,
        string port = null, Dictionary<string, string> connectionProperties = null, bool integratedSecurity = false)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => BuildSqlServer(serverName, dbName, user, password, port, connectionProperties, integratedSecurity),
            Platform.PostgreSQL => BuildPostgreSql(serverName, dbName, user, password, port, connectionProperties),
            Platform.MySQL => BuildMySql(serverName, dbName, user, password, port, connectionProperties),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
        };
    }

    /// <summary>
    /// Rebuilds <paramref name="connectionString"/> to target <paramref name="databaseName"/>, preserving
    /// host, auth, and any other connection options. Returns the input unchanged when either argument
    /// is null/empty, so callers can invoke unconditionally.
    /// </summary>
    public static string RetargetDatabase(string connectionString, string databaseName, Platform platform)
    {
        if (string.IsNullOrEmpty(connectionString)) return connectionString;
        if (string.IsNullOrEmpty(databaseName)) return connectionString;

        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName }.ToString(),
            Platform.PostgreSQL => new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ToString(),
            Platform.MySQL => new MySqlConnectionStringBuilder(connectionString) { Database = databaseName }.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
        };
    }

    /// <summary>
    /// Rebuilds <paramref name="connectionString"/> to reach <paramref name="server"/>, keeping its credentials, port and
    /// every other option. A secondary server is a name sharing the primary's credentials and port settings, so a
    /// --ConnectionString override reaches a secondary with only the server changed. On SQL Server the override's own
    /// ",port" carries over unless the secondary names one.
    /// </summary>
    public static string RetargetServer(string connectionString, string server, Platform platform)
    {
        if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(server)) return connectionString;

        switch (platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                var builder = new SqlConnectionStringBuilder(connectionString);
                var port = builder.DataSource?.IndexOf(',') is int comma and >= 0 ? builder.DataSource[comma..] : "";
                builder.DataSource = server.Contains(',') ? server : server + port;
                return builder.ToString();
            case Platform.PostgreSQL:
                return new NpgsqlConnectionStringBuilder(connectionString) { Host = server }.ToString();
            case Platform.MySQL:
                return new MySqlConnectionStringBuilder(connectionString) { Server = server }.ToString();
            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}");
        }
    }

    /// <summary>
    /// Reads ConnectionProperties from an IConfigurationRoot section.
    /// Returns an empty dictionary if the section doesn't exist.
    /// </summary>
    public static Dictionary<string, string> ReadProperties(IConfigurationRoot config, string section)
    {
        return config.GetSection(section)
            .GetChildren()
            .Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!);
    }

    private static string BuildSqlServer(string serverName, string dbName, string user, string password, string port,
        Dictionary<string, string> connectionProperties, bool integratedSecurity = false)
    {
        // Integrated Security when explicitly requested (Target:IntegratedSecurity=true) OR when no
        // credential is supplied. The explicit flag exists because an override cannot clear a
        // credential a settings file already carries — on Windows, setting an env var to empty deletes
        // it, leaving the file's value in place — so the credential must be superseded, not cleared.
        var security = !integratedSecurity && !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password)
            ? $"User ID={user};Password={password}"
            : "Integrated Security=True";

        // SQL Server uses comma-separated port: "server,port"
        var server = !string.IsNullOrWhiteSpace(port) ? $"{serverName},{port}" : serverName;

        var sb = new StringBuilder($"data source={server};Initial Catalog={dbName};{security};");
        // Declare Encrypt explicitly (default secure) unless the operator overrode it via
        // ConnectionProperties. Matches the Microsoft.Data.SqlClient default (Encrypt=True) — no
        // behavior change — but states transport-security intent instead of relying on a driver
        // default that has flipped across major versions.
        if (!HasProperty(connectionProperties, "Encrypt"))
            sb.Append("Encrypt=True;");
        AppendConnectionProperties(sb, connectionProperties);
        return sb.ToString();
    }

    private static bool HasProperty(Dictionary<string, string> connectionProperties, string key) =>
        connectionProperties != null &&
        connectionProperties.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    private static string BuildPostgreSql(string serverName, string dbName, string user, string password, string port,
        Dictionary<string, string> connectionProperties)
    {
        var portSegment = !string.IsNullOrWhiteSpace(port) ? $"Port={port};" : "";
        var sb = new StringBuilder($"Host={serverName};{portSegment}Database={dbName};Username={user};Password={password};");
        AppendConnectionProperties(sb, connectionProperties);
        return sb.ToString();
    }

    private static string BuildMySql(string serverName, string dbName, string user, string password, string port,
        Dictionary<string, string> connectionProperties)
    {
        var portSegment = !string.IsNullOrWhiteSpace(port) ? $"Port={port};" : "";
        // AllowUserVariables=true is structural — required for PREPARE/EXECUTE dynamic SQL in stored procedures.
        // It must always be present and cannot be overridden via connectionProperties.
        var sb = new StringBuilder($"Server={serverName};{portSegment}Database={dbName};Uid={user};Pwd={password};AllowUserVariables=true;");
        AppendConnectionProperties(sb, connectionProperties, structuralKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AllowUserVariables" });
        return sb.ToString();
    }

    private static void AppendConnectionProperties(StringBuilder sb, Dictionary<string, string> connectionProperties,
        HashSet<string> structuralKeys = null)
    {
        if (connectionProperties == null || connectionProperties.Count == 0)
            return;

        foreach (var kvp in connectionProperties)
        {
            if (structuralKeys != null && structuralKeys.Contains(kvp.Key))
                continue;

            sb.Append($"{kvp.Key}={kvp.Value};");
        }
    }
}
