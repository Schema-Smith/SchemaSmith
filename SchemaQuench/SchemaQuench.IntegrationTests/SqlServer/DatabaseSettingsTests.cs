// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class DatabaseSettingsTests : DatabaseSettingsSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";

    protected override string Kind => "Option";
    protected override string Name => "AUTO_UPDATE_STATISTICS_ASYNC";
    protected override string ValueA => "OFF";
    protected override string ValueB => "ON";
    protected override string EquivalentSpellingOfA => "false";
    protected override (string Name, string Value, string Action) Refused => ("COMPATIBILITY_LEVEL", "1", "fixFailed");

    protected override string ReadCurrent() =>
        Scalar("SELECT CASE is_auto_update_stats_async_on WHEN 1 THEN 'ON' ELSE 'OFF' END FROM sys.databases WHERE name = DB_NAME()")?.ToString();

    protected override void SetCurrent(string value) => Exec($"ALTER DATABASE CURRENT SET AUTO_UPDATE_STATISTICS_ASYNC {value}");
    protected override void Restore() => SetCurrent("OFF");
}
