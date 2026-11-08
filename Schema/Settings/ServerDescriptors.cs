// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Schema.Domain;
using Schema.Domain.Settings;

namespace Schema.Settings;

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
                DECLARE @error NVARCHAR(4000) = ERROR_MESSAGE();
                IF @advanced = 0 BEGIN EXEC sp_configure 'show advanced options', 0; RECONFIGURE; END;
                RAISERROR(@error, 16, 1); -- the re-raise statement arrived in SQL Server 2012; the floor is 2008
            END CATCH
            """;
        SettingSql.Execute(cmd, sql, ("@name", name), ("@value", number));
    }

    public string RenderScript(string name, string value, string database)
    {
        if (!long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            throw new SettingValueException($"'{value}' for Configuration '{name}' is not a number; sp_configure takes one.");
        var n = SettingSql.NLiteral(name);
        return $"IF EXISTS (SELECT 1 FROM sys.configurations WHERE name = {n} AND CAST(value AS BIGINT) <> {number})\r\n" +
               "BEGIN\r\n" +
               "    DECLARE @advanced INT = (SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'show advanced options');\r\n" +
               "    IF @advanced = 0 BEGIN EXEC sp_configure 'show advanced options', 1; RECONFIGURE; END;\r\n" +
               $"    EXEC sp_configure {n}, {number}; RECONFIGURE;\r\n" +
               "    IF @advanced = 0 BEGIN EXEC sp_configure 'show advanced options', 0; RECONFIGURE; END;\r\n" +
               "END\r\n";
    }

    // The documented defaults. An option not listed here is treated as default, so it is captured only when allowlisted.
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["access check cache bucket count"] = "0", ["access check cache quota"] = "0", ["Ad Hoc Distributed Queries"] = "0",
        ["affinity I/O mask"] = "0", ["affinity mask"] = "0", ["allow updates"] = "0", ["backup checksum default"] = "0",
        ["backup compression default"] = "0", ["blocked process threshold (s)"] = "0", ["c2 audit mode"] = "0", ["clr enabled"] = "0",
        ["clr strict security"] = "1", ["contained database authentication"] = "0", ["cost threshold for parallelism"] = "5",
        ["cross db ownership chaining"] = "0", ["cursor threshold"] = "-1", ["Database Mail XPs"] = "0", ["default full-text language"] = "1033",
        ["default language"] = "0", ["default trace enabled"] = "1", ["disallow results from triggers"] = "0", ["external scripts enabled"] = "0",
        ["filestream access level"] = "0", ["fill factor (%)"] = "0", ["index create memory (KB)"] = "0", ["in-doubt xact resolution"] = "0",
        ["lightweight pooling"] = "0", ["locks"] = "0", ["max degree of parallelism"] = "0", ["max full-text crawl range"] = "4",
        ["max server memory (MB)"] = "2147483647", ["max text repl size (B)"] = "65536", ["max worker threads"] = "0", ["media retention"] = "0",
        ["min memory per query (KB)"] = "1024", ["nested triggers"] = "1", ["network packet size (B)"] = "4096",
        ["Ole Automation Procedures"] = "0", ["open objects"] = "0", ["optimize for ad hoc workloads"] = "0", ["PH timeout (s)"] = "60",
        ["precompute rank"] = "0", ["priority boost"] = "0", ["query governor cost limit"] = "0", ["query wait (s)"] = "-1",
        ["recovery interval (min)"] = "0", ["remote access"] = "1", ["remote admin connections"] = "0", ["remote login timeout (s)"] = "10",
        ["remote proc trans"] = "0", ["remote query timeout (s)"] = "600", ["scan for startup procs"] = "0", ["server trigger recursion"] = "1",
        ["set working set size"] = "0", ["SMO and DMO XPs"] = "1", ["transform noise words"] = "0", ["two digit year cutoff"] = "2049",
        ["user connections"] = "0", ["user options"] = "0", ["xp_cmdshell"] = "0",
    };

    // State of this installation, not a choice about a class of server.
    private static readonly HashSet<string> MachineIdentity = new(StringComparer.OrdinalIgnoreCase) { "show advanced options", "Agent XPs" };

    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database) =>
        SettingSql.Rows(cmd, "SELECT name, CAST(value AS NVARCHAR(100)) FROM sys.configurations")
            .Select(r => new CapturedSetting(Kind, r[0], r[1], !Defaults.TryGetValue(r[0], out var d) || d == r[1], MachineIdentity.Contains(r[0])));
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

    public string RenderScript(string name, string value, string database)
    {
        var flag = Flag(name) ?? throw new SettingValueException("a trace flag is named by its number");
        var on = SettingValueComparer.Boolish(value, "ON");
        return $"-- Lasts until the next restart; add -T{flag} to the startup parameters to keep it.\r\nDBCC {(on ? "TRACEON" : "TRACEOFF")}({flag}, -1);\r\n";
    }

    // Each trace flag on globally is one somebody turned on.
    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database) =>
        SettingSql.Rows(cmd, """
            CREATE TABLE #status (TraceFlag INT, Status INT, Global INT, Session INT);
            INSERT #status EXEC('DBCC TRACESTATUS(-1) WITH NO_INFOMSGS');
            SELECT CAST(TraceFlag AS NVARCHAR(10)) FROM #status WHERE Global = 1;
            DROP TABLE #status;
            """).Select(r => new CapturedSetting(Kind, r[0], "ON", false));
}

/// <summary>PostgreSQL server <c>Parameter</c>: <c>ALTER SYSTEM</c>, then a configuration reload. A <c>postmaster</c> parameter
/// waits for a restart, which <c>pg_settings.pending_restart</c> reports.</summary>
public sealed class PostgreSqlServerParameterDescriptor : ISettingDescriptor
{
    private string _unit;
    private string _name;

    public string Kind => SettingKinds.Parameter;
    public SettingScope Scope => SettingScope.Server;
    public bool PersistsAcrossRestart => true;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        _name = name;
        var row = SettingSql.FirstRow(cmd, "SELECT setting, unit, context, CASE WHEN pending_restart THEN 1 ELSE 0 END, source, boot_val FROM pg_settings WHERE name = lower(@name)",
            ("@name", name));
        if (row == null) return SettingReading.Missing($"'{name}' is not a parameter on this server");
        _unit = row[1];
        var unfixable = row[2] == "internal" ? "an internal parameter is fixed when the server is built" : null;
        if (row[4] is not ("user" or "database" or "client" or "session"))
            return new SettingReading(true, row[0], row[0], row[3] == "1", unfixable);

        // The deploy login's own value (ALTER ROLE / ALTER DATABASE) hides the server's, and ALTER SYSTEM cannot change it:
        // read the server's from its configuration files instead, or the built-in value when they do not set it.
        try
        {
            var file = SettingSql.FirstRow(cmd, "SELECT setting FROM pg_file_settings WHERE name = lower(@name) AND error IS NULL ORDER BY seqno DESC LIMIT 1",
                ("@name", name));
            var server = file?[0] ?? row[5];
            return new SettingReading(true, server, server, row[3] == "1", unfixable);
        }
        catch (DbException)
        {
            return SettingReading.Missing($"the deploy login sets its own '{name}', which hides the server's value, and it cannot read pg_file_settings to see past it");
        }
    }

    public bool Equivalent(string declared, SettingReading current) =>
        PostgreSqlLists.IsList(_name) ? PostgreSqlLists.Same(declared, current.Configured) : SettingValueComparer.PostgreSql(declared, current.Configured, _unit);

    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        SettingSql.Execute(cmd, $"ALTER SYSTEM SET {SettingSql.DoubleQuote(name.ToLowerInvariant())} = {PostgreSqlLists.Literal(name, value)}");
        SettingSql.Execute(cmd, "SELECT pg_reload_conf()");
        // The reload is a signal the server acts on in its own time: wait briefly for the value (or the pending restart) to show.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var now = Read(cmd, name, database);
            if (now.PendingRestart == true || (now.Exists && Equivalent(value, now))) return;
            System.Threading.Thread.Sleep(100);
        }
    }

    // ALTER SYSTEM cannot run inside a transaction block, so not inside a DO block either; repeating it is harmless.
    public string RenderScript(string name, string value, string database) =>
        $"ALTER SYSTEM SET {SettingSql.DoubleQuote(name.ToLowerInvariant())} = {PostgreSqlLists.Literal(name, value)};\r\nSELECT pg_reload_conf();\r\n";

    internal static readonly HashSet<string> MachineIdentity = new(StringComparer.OrdinalIgnoreCase)
    {
        "data_directory", "config_file", "hba_file", "ident_file", "external_pid_file", "listen_addresses", "port", "cluster_name",
        "ssl_cert_file", "ssl_key_file", "ssl_ca_file", "ssl_crl_file", "ssl_crl_dir", "ssl_dh_params_file", "unix_socket_directories",
        "log_directory", "krb_server_keyfile"
    };

    // A setting from a configuration file or the command line is one somebody chose; everything else is the build's default.
    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database) =>
        SettingSql.Rows(cmd, "SELECT name, setting, source FROM pg_settings")
            .Select(r => new CapturedSetting(Kind, r[0], r[1], r[2] is not ("configuration file" or "command line"), MachineIdentity.Contains(r[0])));
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

    // A comma list (sql_mode, optimizer_switch) is a set: the server reorders it.
    public bool Equivalent(string declared, SettingReading current)
    {
        if ((declared ?? "").Contains(',') || (current.Configured ?? "").Contains(','))
            return Items(declared).SetEquals(Items(current.Configured));
        return SettingValueComparer.Boolish(declared, current.Configured);
    }

    private static HashSet<string> Items(string value) =>
        (value ?? "").Split(',').Select(i => i.Trim()).Where(i => i.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    public string RenderScript(string name, string value, string database)
    {
        if (!VariableName.IsMatch(name ?? "")) throw new SettingValueException($"'{name}' is not a variable name.");
        var v = (value ?? "").Trim();
        return $"SET {(CanPersist ? "PERSIST" : "GLOBAL")} {name} = {(Bare.IsMatch(v) ? v : SettingSql.MySqlLiteral(v))};\r\n";
    }

    private static readonly HashSet<string> MachineIdentity = new(StringComparer.OrdinalIgnoreCase)
    {
        "datadir", "basedir", "tmpdir", "socket", "port", "hostname", "server_id", "server_uuid", "log_bin_basename", "log_bin_index",
        "relay_log", "relay_log_basename", "relay_log_index", "pid_file", "general_log_file", "slow_query_log_file", "log_error",
        "innodb_data_home_dir", "innodb_log_group_home_dir", "secure_file_priv", "plugin_dir", "character_sets_dir", "lc_messages_dir"
    };

    // MySQL 8 says where each value came from; MariaDB keeps each variable's default; MySQL 5.7 has neither, so nothing
    // counts as changed there and only an allowlist captures anything.
    public IEnumerable<CapturedSetting> Enumerate(IDbCommand cmd, string database)
    {
        if (CanPersist)
            return SettingSql.Rows(cmd, "SELECT v.VARIABLE_NAME, g.VARIABLE_VALUE, v.VARIABLE_SOURCE FROM performance_schema.variables_info v " +
                                        "JOIN performance_schema.global_variables g ON g.VARIABLE_NAME = v.VARIABLE_NAME")
                .Select(r => new CapturedSetting(Kind, r[0], r[1], r[2] == "COMPILED", MachineIdentity.Contains(r[0])));
        if (platform == Platform.MariaDb)
            return SettingSql.Rows(cmd, "SELECT LOWER(VARIABLE_NAME), GLOBAL_VALUE, DEFAULT_VALUE FROM information_schema.SYSTEM_VARIABLES WHERE GLOBAL_VALUE IS NOT NULL")
                .Select(r => new CapturedSetting(Kind, r[0], r[1], r[2] == null || SettingValueComparer.Boolish(r[1], r[2]), MachineIdentity.Contains(r[0])));
        return SettingSql.Rows(cmd, "SHOW GLOBAL VARIABLES").Select(r => new CapturedSetting(Kind, r[0], r[1], true, MachineIdentity.Contains(r[0])));
    }
}
