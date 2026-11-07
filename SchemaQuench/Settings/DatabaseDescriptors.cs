// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.Domain;
using Schema.Domain.Settings;

namespace SchemaQuench.Settings;

/// <summary>SQL Server <c>ScopedConfiguration</c>: <c>sys.database_scoped_configurations</c> in the database the command is in.</summary>
public sealed class SqlServerScopedConfigurationDescriptor(int serverMajorVersion) : ISettingDescriptor
{
    public string Kind => SettingKinds.ScopedConfiguration;
    public SettingScope Scope => SettingScope.Database;
    public bool PersistsAcrossRestart => true;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        if (serverMajorVersion < 13) return SettingReading.Missing("database-scoped configuration needs SQL Server 2016");
        var row = SettingSql.FirstRow(cmd, "SELECT CAST(value AS NVARCHAR(4000)) FROM sys.database_scoped_configurations WHERE name = @name", ("@name", name));
        return row == null
            ? SettingReading.Missing($"'{name}' is not a database-scoped configuration on this server")
            : new SettingReading(true, row[0], row[0], false, null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.Boolish(declared, current.Configured);

    public void Apply(IDbCommand cmd, string name, string value, string database) =>
        SettingSql.Execute(cmd, $"ALTER DATABASE SCOPED CONFIGURATION SET {SettingSql.BracketQuote(name.ToUpperInvariant())} = " +
                                SettingSql.RequireToken(value, $"ScopedConfiguration '{name}'"));
}

/// <summary>PostgreSQL database <c>Parameter</c>: the database's own setting (<c>pg_db_role_setting</c>), or the server's when
/// the database has none, which is what a new session in it sees.</summary>
public sealed class PostgreSqlDatabaseParameterDescriptor : ISettingDescriptor
{
    private string _unit;

    public string Kind => SettingKinds.Parameter;
    public SettingScope Scope => SettingScope.Database;
    public bool PersistsAcrossRestart => true;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        const string sql = """
            SELECT s.setting, s.unit, s.context,
                   (SELECT substr(x, length(s.name) + 2)
                      FROM pg_db_role_setting r CROSS JOIN LATERAL unnest(r.setconfig) AS x
                     WHERE r.setrole = 0 AND r.setdatabase = (SELECT oid FROM pg_database WHERE datname = @db)
                       AND x LIKE s.name || '=%' LIMIT 1)
              FROM pg_settings s WHERE s.name = lower(@name)
            """;
        var row = SettingSql.FirstRow(cmd, sql, ("@db", database), ("@name", name));
        if (row == null) return SettingReading.Missing($"'{name}' is not a parameter on this server");
        _unit = row[1];
        var configured = row[3] ?? row[0];
        return row[2] is "postmaster" or "internal"
            ? new SettingReading(true, configured, configured, false, $"a {row[2]} parameter cannot be set per database")
            : new SettingReading(true, configured, configured, false, null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.PostgreSql(declared, current.Configured, _unit);

    public void Apply(IDbCommand cmd, string name, string value, string database) =>
        SettingSql.Execute(cmd, $"ALTER DATABASE {SettingSql.DoubleQuote(database)} SET {SettingSql.DoubleQuote(name.ToLowerInvariant())} = {SettingSql.StandardLiteral(value)}");
}

/// <summary>MySQL / MariaDB <c>Option</c>: a database's default character set and collation.</summary>
public sealed class MySqlOptionDescriptor : ISettingDescriptor
{
    public string Kind => SettingKinds.Option;
    public SettingScope Scope => SettingScope.Database;
    public bool PersistsAcrossRestart => true;

    private static int? Column(string name) => name?.Trim().ToUpperInvariant() switch
    {
        "CHARACTER SET" => 0,
        "COLLATE" => 1,
        _ => null
    };

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        if (Column(name) is not { } column)
            return SettingReading.Missing($"'{name}' is not a database option SchemaSmith reads: use CHARACTER SET or COLLATE");
        var row = SettingSql.FirstRow(cmd, "SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @db",
            ("@db", database));
        return row == null ? SettingReading.Missing($"database '{database}' was not found") : new SettingReading(true, row[column], row[column], false, null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.CaseInsensitive(declared, current.Configured);

    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        var keyword = Column(name) == 0 ? "CHARACTER SET" : "COLLATE";
        SettingSql.Execute(cmd, $"ALTER DATABASE {SettingSql.BacktickQuote(database)} {keyword} = {SettingSql.MySqlLiteral(value)}");
    }
}

public static class SettingDescriptors
{
    public static ISettingDescriptor For(Platform platform, SettingScope scope, string kind, int serverMajorVersion)
    {
        var k = kind?.ToLowerInvariant();
        return (platform.GetBasePlatform(), scope, k) switch
        {
            (Platform.SqlServer, SettingScope.Database, "option") => new SqlServerOptionDescriptor(),
            (Platform.SqlServer, SettingScope.Database, "scopedconfiguration") => new SqlServerScopedConfigurationDescriptor(serverMajorVersion),
            (Platform.PostgreSQL, SettingScope.Database, "parameter") => new PostgreSqlDatabaseParameterDescriptor(),
            (Platform.MySQL, SettingScope.Database, "option") => new MySqlOptionDescriptor(),
            _ => throw new ArgumentException($"No {scope.ToString().ToLowerInvariant()} setting kind '{kind}' on {platform}.", nameof(kind))
        };
    }
}
