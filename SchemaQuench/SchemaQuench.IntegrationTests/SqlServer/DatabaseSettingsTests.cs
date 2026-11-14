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

    protected override string SetSql(string value) => $"ALTER DATABASE CURRENT SET AUTO_UPDATE_STATISTICS_ASYNC {value}";
    protected override void Restore() => SetCurrent("OFF");

    // The termination clause is refused for a versioning change (Msg 5083), so this one is sent without it.
    [Test]
    public void AllowSnapshotIsolation_IsFixed()
    {
        Exec("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION OFF");
        try
        {
            Package(One("ON", fix: true, "Error", name: "ALLOW_SNAPSHOT_ISOLATION"));

            var run = Deploy();

            Assert.Multiple(() =>
            {
                Assert.That(SettingRows(run), Is.EqualTo(new[] { "Option ALLOW_SNAPSHOT_ISOLATION changed" }), Lines(run));
                Assert.That(Scalar("SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()")?.ToString(), Is.EqualTo("1"));
            });
        }
        finally
        {
            Exec("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION OFF");
        }
    }
}
