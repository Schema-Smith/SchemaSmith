// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Settings;

namespace Schema.UnitTests.Settings;

// An equivalent spelling is not drift: each kind compares the engine's way.
[TestFixture]
public class SettingValueComparerTests
{
    [TestCase("ON", "1", true)]
    [TestCase("on", "true", true)]
    [TestCase("Yes", "ON", true)]
    [TestCase("OFF", "0", true)]
    [TestCase("false", "no", true)]
    [TestCase("ON", "0", false)]
    [TestCase("SIMPLE", "simple", true)]
    [TestCase("SIMPLE", "FULL", false)]
    [TestCase("4", "4", true)]
    [TestCase("4", "04", true)]
    public void Boolish(string declared, string current, bool equal)
    {
        Assert.That(SettingValueComparer.Boolish(declared, current), Is.EqualTo(equal));
    }

    [TestCase("128MB", "16384", "8kB", true)]
    [TestCase("1GB", "16384", "8kB", false)]
    [TestCase("16384", "16384", "8kB", true)]
    [TestCase("8MB", "8192", "kB", true)]
    [TestCase("8MB", "8MB", "kB", true)]
    [TestCase("8192kB", "8MB", "kB", true)]
    [TestCase("30s", "30000", "ms", true)]
    [TestCase("1min", "60", "s", true)]
    [TestCase("250ms", "250", "ms", true)]
    [TestCase("1h", "60", "min", true)]
    [TestCase("on", "on", null, true)]
    [TestCase("true", "on", null, true)]
    [TestCase("read committed", "Read Committed", null, true)]
    [TestCase("'UTC'", "UTC", null, true)]
    [TestCase("4MB", "8192", "kB", false)]
    public void PostgreSqlUnits(string declared, string current, string unit, bool equal)
    {
        Assert.That(SettingValueComparer.PostgreSql(declared, current, unit), Is.EqualTo(equal));
    }
}
