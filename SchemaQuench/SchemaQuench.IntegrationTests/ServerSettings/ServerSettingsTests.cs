// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.ServerSettings;

[Category("DisposableServerSqlServer")]
[TestFixture]
public class SqlServerServerSettingsTests : ServerSettingsSharedTests
{
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cost threshold for parallelism"] = "5",
        ["user connections"] = "0"
    };

    protected override Platform Platform => Platform.SqlServer;
    protected override string ConfigPrefix => "SqlServer";
    protected override string Kind => "Configuration";
    protected override string DynamicName => "cost threshold for parallelism";
    protected override string DynamicValue => "50";
    protected override (string Name, string Value)? PendingRestart => ("user connections", "500");

    protected override string ReadValue(string name) =>
        Scalar($"SELECT CAST(value AS NVARCHAR(100)) FROM sys.configurations WHERE name = '{name}'")?.ToString();

    protected override void ResetSetting(string name) => Exec($"""
        EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
        EXEC sp_configure '{name}', {_defaults[name]}; RECONFIGURE;
        """);
}

[Category("DisposableServerPostgreSQL")]
[TestFixture]
public class PostgreSqlServerSettingsTests : ServerSettingsSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string Kind => "Parameter";
    protected override string DynamicName => "log_min_duration_statement";
    protected override string DynamicValue => "250";
    protected override (string Name, string Value)? PendingRestart => ("max_connections", "120");

    // What the server will use: the value written by ALTER SYSTEM once reloaded, the setting otherwise.
    protected override string ReadValue(string name) => Scalar($"SELECT setting FROM pg_settings WHERE name = '{name}'")?.ToString();

    protected override void ResetSetting(string name)
    {
        Exec($"ALTER SYSTEM RESET {name}");
        Exec("SELECT pg_reload_conf()");
    }

    // A login with its own value (ALTER ROLE) sees that value, not the server's; the step must compare and fix the server's.
    [Test]
    public void ALoginsOwnValue_DoesNotHideTheServers()
    {
        Exec("ALTER ROLE CURRENT_USER SET log_min_duration_statement = 1000");
        try
        {
            var first = RunDeclared(DynamicName, DynamicValue);
            var second = RunDeclared(DynamicName, DynamicValue);

            Assert.That(first, Is.EqualTo(new[] { $"{Kind} {DynamicName} changed" }));
            Assert.That(second, Is.Empty, "the server's value now matches, whatever the login's own is");
        }
        finally
        {
            Exec("ALTER ROLE CURRENT_USER RESET log_min_duration_statement");
        }
    }
}

[Category("DisposableServerMySQL")]
[TestFixture]
public class MySqlServerSettingsTests : ServerSettingsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ConfigPrefix => "MySQL";
    protected override string Kind => "Variable";
    protected override string DynamicName => "max_connections";
    protected override string DynamicValue => "180";
    protected override (string Name, string Value)? PendingRestart => ("back_log", "100");
    protected override string[] ReadOnlyNames => ["lower_case_table_names", "log_bin"];

    protected override string ReadValue(string name) => Scalar($"SELECT @@GLOBAL.{name}")?.ToString();

    protected override void ResetSetting(string name)
    {
        Exec($"RESET PERSIST IF EXISTS {name}");
        if (name == "max_connections") Exec("SET GLOBAL max_connections = 151");
    }
}

[Category("DisposableServerMariaDb")]
[TestFixture]
public class MariaDbServerSettingsTests : ServerSettingsSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";
    protected override string Kind => "Variable";
    protected override string DynamicName => "max_connections";
    protected override string DynamicValue => "180";
    protected override (string Name, string Value)? PendingRestart => null;
    protected override string[] ReadOnlyNames => ["lower_case_table_names", "aria_used_for_temp_tables"];

    protected override string ReadValue(string name) => Scalar($"SELECT @@GLOBAL.{name}")?.ToString();
    protected override void ResetSetting(string name) => Exec("SET GLOBAL max_connections = 151");

    // SET GLOBAL is all MariaDB has: the change is in effect now and lost at the next restart, which the run says.
    protected override bool FixPersists => false;
}
