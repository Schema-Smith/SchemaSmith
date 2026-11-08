// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.Domain;
using Schema.Domain.Settings;

namespace Schema.Settings;

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

    // The catalog keeps a switch as 1 / 0, so a captured value is written as ON / OFF unless the configuration is a number.
    private static readonly HashSet<string> Numeric = new(StringComparer.OrdinalIgnoreCase) { "MAXDOP", "PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES" };

    public string RenderScript(string name, string value, string database)
    {
        var v = SettingSql.RequireToken(value, $"ScopedConfiguration '{name}'");
        var accepted = v switch { "ON" => "N'ON', N'1'", "OFF" => "N'OFF', N'0'", _ => SettingSql.NLiteral(v) };
        return $"IF NOT EXISTS (SELECT 1 FROM sys.database_scoped_configurations WHERE name = {SettingSql.NLiteral(name.ToUpperInvariant())} AND CAST(value AS NVARCHAR(4000)) IN ({accepted}))\r\n" +
               $"    ALTER DATABASE SCOPED CONFIGURATION SET {SettingSql.BracketQuote(name.ToUpperInvariant())} = {v};\r\n";
    }

    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database)
    {
        if (serverMajorVersion < 13) return [];
        // is_value_default arrives in 2017; on 2016 a switch that is off, or zero, is the default.
        var sql = serverMajorVersion >= 14
            ? "SELECT name, CAST(value AS NVARCHAR(4000)), CAST(is_value_default AS INT) FROM sys.database_scoped_configurations"
            : "SELECT name, CAST(value AS NVARCHAR(4000)), CASE WHEN CAST(value AS NVARCHAR(4000)) IN (N'0', N'OFF', N'') THEN 1 ELSE 0 END FROM sys.database_scoped_configurations";
        return SettingSql.Rows(cmd, sql).Select(r => new CapturedSetting(Kind, r[0], Numeric.Contains(r[0]) ? r[1] : r[1] switch { "1" => "ON", "0" => "OFF", _ => r[1] }, r[2] == "1"));
    }
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

    // Repeating ALTER DATABASE SET is harmless, so the script needs no comparison; it names the database it runs in.
    public string RenderScript(string name, string value, string database) =>
        $"DO $$ BEGIN EXECUTE format('ALTER DATABASE %I SET {SettingSql.DoubleQuote(name.ToLowerInvariant()).Replace("'", "''")} = %L', current_database(), {SettingSql.StandardLiteral(value)}); END $$;\r\n";

    // Every setting the database carries itself is one somebody chose.
    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database) =>
        SettingSql.Rows(cmd, """
            SELECT x FROM pg_db_role_setting r CROSS JOIN LATERAL unnest(r.setconfig) AS x
             WHERE r.setrole = 0 AND r.setdatabase = (SELECT oid FROM pg_database WHERE datname = @db)
            """, ("@db", database))
            .Select(r => r[0].Split('=', 2))
            .Select(p => new CapturedSetting(Kind, p[0], p.Length > 1 ? p[1] : "", false, PostgreSqlServerParameterDescriptor.MachineIdentity.Contains(p[0])));
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

    // Without a name ALTER DATABASE changes the default database, which is the one a template's Setup script runs in.
    public string RenderScript(string name, string value, string database) =>
        $"ALTER DATABASE {(Column(name) == 0 ? "CHARACTER SET" : "COLLATE")} = {SettingSql.MySqlLiteral(value)};\r\n";

    // Captured when it differs from the server's default for a new database.
    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database)
    {
        var row = SettingSql.FirstRow(cmd, "SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME, @@character_set_server, @@collation_server " +
                                           "FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @db", ("@db", database));
        if (row == null) return [];
        return
        [
            new CapturedSetting(Kind, "CHARACTER SET", row[0], SettingValueComparer.CaseInsensitive(row[0], row[2])),
            new CapturedSetting(Kind, "COLLATE", row[1], SettingValueComparer.CaseInsensitive(row[1], row[3]))
        ];
    }
}

public static class SettingDescriptors
{
    // TargetVersionDetector's comparable: the major on SQL Server and PostgreSQL, major * 100 + minor on the MySQL family.
    public static int MajorVersion(Platform platform, int serverComparable) =>
        platform.GetBasePlatform() == Platform.MySQL ? serverComparable / 100 : serverComparable;

    public static ISettingDescriptor For(Platform platform, SettingScope scope, string kind, int serverMajorVersion)
    {
        var k = kind?.ToLowerInvariant();
        return (platform.GetBasePlatform(), scope, k) switch
        {
            (Platform.SqlServer, SettingScope.Database, "option") => new SqlServerOptionDescriptor(),
            (Platform.SqlServer, SettingScope.Database, "scopedconfiguration") => new SqlServerScopedConfigurationDescriptor(serverMajorVersion),
            (Platform.PostgreSQL, SettingScope.Database, "parameter") => new PostgreSqlDatabaseParameterDescriptor(),
            (Platform.MySQL, SettingScope.Database, "option") => new MySqlOptionDescriptor(),
            (Platform.SqlServer, SettingScope.Server, "configuration") => new SqlServerConfigurationDescriptor(),
            (Platform.SqlServer, SettingScope.Server, "traceflag") => new SqlServerTraceFlagDescriptor(),
            (Platform.PostgreSQL, SettingScope.Server, "parameter") => new PostgreSqlServerParameterDescriptor(),
            (Platform.MySQL, SettingScope.Server, "variable") => new MySqlVariableDescriptor(platform, serverMajorVersion),
            _ => throw new ArgumentException($"No {scope.ToString().ToLowerInvariant()} setting kind '{kind}' on {platform}.", nameof(kind))
        };
    }
}
