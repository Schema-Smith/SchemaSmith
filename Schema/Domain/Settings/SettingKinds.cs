// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;

namespace Schema.Domain.Settings;

/// <summary>The catalog a setting lives in, per engine and scope. A bare name is ambiguous: SQL Server's MAXDOP is both a
/// server configuration and a database-scoped configuration.</summary>
public static class SettingKinds
{
    public const string Configuration = "Configuration";
    public const string TraceFlag = "TraceFlag";
    public const string Option = "Option";
    public const string ScopedConfiguration = "ScopedConfiguration";
    public const string Parameter = "Parameter";
    public const string Variable = "Variable";

    public static IReadOnlyList<string> For(Platform platform, SettingScope scope) => (platform.GetBasePlatform(), scope) switch
    {
        (Platform.SqlServer, SettingScope.Server) => [Configuration, TraceFlag],
        (Platform.SqlServer, SettingScope.Database) => [Option, ScopedConfiguration],
        (Platform.PostgreSQL, _) => [Parameter],
        (Platform.MySQL, SettingScope.Server) => [Variable],
        (Platform.MySQL, SettingScope.Database) => [Option],
        _ => []
    };
}
