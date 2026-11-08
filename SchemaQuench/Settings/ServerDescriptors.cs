// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Schema.Domain;
using Schema.Domain.Settings;

namespace SchemaQuench.Settings;

/// <summary>SQL Server <c>Configuration</c>: <c>sys.configurations</c>, changed with <c>sp_configure</c> and <c>RECONFIGURE</c>.
/// A change waits for a restart when the configured value and the value in use differ afterwards.</summary>
public sealed class SqlServerConfigurationDescriptor : ISettingDescriptor
{
    public string Kind => SettingKinds.Configuration;
    public SettingScope Scope => SettingScope.Server;
    public bool PersistsAcrossRestart => true;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        var row = SettingSql.FirstRow(cmd, "SELECT CAST(value AS NVARCHAR(100)), CAST(value_in_use AS NVARCHAR(100)) FROM sys.configurations WHERE name = @name",
            ("@name", name));
        return row == null
            ? SettingReading.Missing($"'{name}' is not a server configuration option on this version")
            : new SettingReading(true, row[0], row[1], row[0] != row[1], null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.Boolish(declared, current.Configured);

    // 'show advanced options' is turned on only for the change and put back as it was, even when the change fails.
    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            throw new SettingValueException($"'{value}' for Configuration '{name}' is not a number; sp_configure takes one.");
        const string sql = """
            DECLARE @advanced INT = (SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'show advanced options');
            BEGIN TRY
                IF @advanced = 0 BEGIN EXEC sp_configure 'show advanced options', 1; RECONFIGURE; END;
                EXEC sp_configure @name, @value; RECONFIGURE;
                IF @advanced = 0 BEGIN EXEC sp_configure 'show advanced options', 0; RECONFIGURE; END;
            END TRY
            BEGIN CATCH
                IF @advanced = 0 BEGIN EXEC sp_configure 'show advanced options', 0; RECONFIGURE; END;
                THROW;
            END CATCH
            """;
        SettingSql.Execute(cmd, sql, ("@name", name), ("@value", number));
    }
}

/// <summary>SQL Server <c>TraceFlag</c>, global. <c>DBCC TRACEON</c> lasts until the next restart; only a startup parameter keeps it.</summary>
public sealed class SqlServerTraceFlagDescriptor : ISettingDescriptor
{
    public string Kind => SettingKinds.TraceFlag;
    public SettingScope Scope => SettingScope.Server;
    public bool PersistsAcrossRestart => false;

    public string RevertAdvice(string name) => $"add -T{name} to the startup parameters to keep it";

    private static int? Flag(string name) => int.TryParse(name?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        if (Flag(name) is not { } flag) return SettingReading.Missing("a trace flag is named by its number");
        var row = SettingSql.FirstRow(cmd, $"""
            CREATE TABLE #status (TraceFlag INT, Status INT, Global INT, Session INT);
            INSERT #status EXEC('DBCC TRACESTATUS({flag}, -1) WITH NO_INFOMSGS');
            SELECT CASE WHEN MAX(Global) = 1 THEN 'ON' ELSE 'OFF' END FROM #status;
            DROP TABLE #status;
            """);
        return new SettingReading(true, row?[0] ?? "OFF", row?[0] ?? "OFF", false, null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.Boolish(declared, current.Configured);

    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        var flag = Flag(name) ?? throw new SettingValueException("a trace flag is named by its number");
        var on = SettingValueComparer.Boolish(value, "ON");
        if (!on && !SettingValueComparer.Boolish(value, "OFF"))
            throw new SettingValueException($"'{value}' for TraceFlag '{name}' is not ON or OFF.");
        SettingSql.Execute(cmd, $"DBCC {(on ? "TRACEON" : "TRACEOFF")}({flag}, -1)");
    }
}

/// <summary>PostgreSQL server <c>Parameter</c>: <c>ALTER SYSTEM</c>, then a configuration reload. A <c>postmaster</c> parameter
/// waits for a restart, which <c>pg_settings.pending_restart</c> reports.</summary>
public sealed class PostgreSqlServerParameterDescriptor : ISettingDescriptor
{
    private string _unit;

    public string Kind => SettingKinds.Parameter;
    public SettingScope Scope => SettingScope.Server;
    public bool PersistsAcrossRestart => true;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        var row = SettingSql.FirstRow(cmd, "SELECT setting, unit, context, CASE WHEN pending_restart THEN 1 ELSE 0 END FROM pg_settings WHERE name = lower(@name)",
            ("@name", name));
        if (row == null) return SettingReading.Missing($"'{name}' is not a parameter on this server");
        _unit = row[1];
        return new SettingReading(true, row[0], row[0], row[3] == "1", row[2] == "internal" ? "an internal parameter is fixed when the server is built" : null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.PostgreSql(declared, current.Configured, _unit);

    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        SettingSql.Execute(cmd, $"ALTER SYSTEM SET {SettingSql.DoubleQuote(name.ToLowerInvariant())} = {SettingSql.StandardLiteral(value)}");
        SettingSql.Execute(cmd, "SELECT pg_reload_conf()");
    }
}

/// <summary>MySQL / MariaDB <c>Variable</c>. MySQL 8 persists with <c>SET PERSIST</c> (a read-only variable with
/// <c>SET PERSIST_ONLY</c>, for the next start); MySQL 5.7 and MariaDB have only <c>SET GLOBAL</c>, which a restart loses.</summary>
public sealed class MySqlVariableDescriptor(Platform platform, int serverMajorVersion) : ISettingDescriptor
{
    private static readonly Regex VariableName = new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Bare = new(@"^(-?\d+(\.\d+)?|ON|OFF)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private bool CanPersist => platform == Platform.MySQL && serverMajorVersion >= 8;

    public string Kind => SettingKinds.Variable;
    public SettingScope Scope => SettingScope.Server;
    public bool PersistsAcrossRestart => CanPersist;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        if (!VariableName.IsMatch(name ?? "")) return SettingReading.Missing($"'{name}' is not a variable name");
        var row = SettingSql.FirstRow(cmd, "SHOW GLOBAL VARIABLES WHERE Variable_name = @name", ("@name", name));
        if (row == null) return SettingReading.Missing($"'{name}' is not a variable on this server");
        var running = row[1];
        if (!CanPersist) return new SettingReading(true, running, running, null, null);

        // A PERSIST_ONLY value differing from the running one is waiting for a restart.
        string persisted = null;
        try
        {
            persisted = SettingSql.FirstRow(cmd, "SELECT VARIABLE_VALUE FROM performance_schema.persisted_variables WHERE VARIABLE_NAME = @name", ("@name", name))?[0];
        }
        catch (DbException)
        {
            // performance_schema off: the running value is all there is to compare.
        }
        var pending = persisted != null && !SettingValueComparer.Boolish(persisted, running);
        return new SettingReading(true, persisted ?? running, running, pending, null);
    }

    public bool Equivalent(string declared, SettingReading current) => SettingValueComparer.Boolish(declared, current.Configured);

    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        if (!VariableName.IsMatch(name ?? "")) throw new SettingValueException($"'{name}' is not a variable name.");
        var v = (value ?? "").Trim();
        var literal = Bare.IsMatch(v) ? v : SettingSql.MySqlLiteral(v);
        try
        {
            SettingSql.Execute(cmd, $"SET {(CanPersist ? "PERSIST" : "GLOBAL")} {name} = {literal}");
        }
        catch (DbException e) when (IsReadOnly(e))
        {
            if (!CanPersist) throw new SettingUnfixableException("read-only variable; set it in the server's option file");
            SettingSql.Execute(cmd, $"SET PERSIST_ONLY {name} = {literal}");
        }
    }

    private static bool IsReadOnly(DbException e) => e.Message.Contains("read only variable", StringComparison.OrdinalIgnoreCase);
}
