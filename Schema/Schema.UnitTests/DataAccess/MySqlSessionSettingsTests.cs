// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.DataAccess;

namespace Schema.UnitTests.DataAccess;

[TestFixture]
public class MySqlSessionSettingsTests
{
    [TestCase("", "")]
    [TestCase("STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION", "STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION")]
    [TestCase("ANSI_QUOTES,STRICT_TRANS_TABLES,NO_BACKSLASH_ESCAPES", "STRICT_TRANS_TABLES")]
    [TestCase("PIPES_AS_CONCAT", "")]
    // MySQL reports ANSI expanded; REAL_AS_FLOAT and ONLY_FULL_GROUP_BY change semantics, not parsing, so they stay.
    [TestCase("REAL_AS_FLOAT,PIPES_AS_CONCAT,ANSI_QUOTES,IGNORE_SPACE,ONLY_FULL_GROUP_BY,ANSI", "REAL_AS_FLOAT,ONLY_FULL_GROUP_BY")]
    // MariaDB's ORACLE expansion.
    [TestCase("PIPES_AS_CONCAT,ANSI_QUOTES,IGNORE_SPACE,ORACLE,NO_KEY_OPTIONS,NO_TABLE_OPTIONS,NO_FIELD_OPTIONS,NO_AUTO_CREATE_USER,SIMULTANEOUS_ASSIGNMENT",
        "NO_AUTO_CREATE_USER")]
    [TestCase("ansi_quotes, strict_all_tables", "strict_all_tables")]
    public void ParseNeutral_RemovesOnlyTheFlagsThatChangeParsing(string mode, string expected) =>
        Assert.That(MySqlSessionSettings.ParseNeutral(mode), Is.EqualTo(expected));

    [Test]
    public void ParseNeutral_AddsARequestedFlagOnce()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MySqlSessionSettings.ParseNeutral("STRICT_TRANS_TABLES,ANSI_QUOTES", "NO_AUTO_VALUE_ON_ZERO"),
                Is.EqualTo("STRICT_TRANS_TABLES,NO_AUTO_VALUE_ON_ZERO"));
            Assert.That(MySqlSessionSettings.ParseNeutral("NO_AUTO_VALUE_ON_ZERO", "NO_AUTO_VALUE_ON_ZERO"),
                Is.EqualTo("NO_AUTO_VALUE_ON_ZERO"));
        });
    }

    [Test]
    public void WithTimeZone_PinsTheOffsetAroundTheScript_AndRestoresTheSessionZone()
    {
        var wrapped = MySqlSessionSettings.WithTimeZone("INSERT INTO t VALUES (1);", "+00:00");
        Assert.That(wrapped, Is.EqualTo(
            "SET @ss_saved_time_zone = @@SESSION.time_zone;\n" +
            "SET SESSION time_zone = '+00:00';\n" +
            "INSERT INTO t VALUES (1);\n" +
            "SET SESSION time_zone = @ss_saved_time_zone;\n"));
    }

    // The value is spliced into SQL, so anything that is not a plain offset is refused rather than quoted.
    [TestCase("UTC")]
    [TestCase("+0:00")]
    [TestCase("+15:00")]
    [TestCase("+00:00'; DROP TABLE x; --")]
    [TestCase(null)]
    public void WithTimeZone_RefusesAnythingButAnOffset(string zone) =>
        Assert.Throws<System.ArgumentException>(() => MySqlSessionSettings.WithTimeZone("SELECT 1;", zone));

    [Test]
    public void ParseNeutral_OfNull_IsEmpty() => Assert.That(MySqlSessionSettings.ParseNeutral(null), Is.Empty);
}
