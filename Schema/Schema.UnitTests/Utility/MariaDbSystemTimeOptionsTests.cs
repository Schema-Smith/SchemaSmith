// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class MariaDbSystemTimeOptionsTests
{
    // The texts are what SHOW CREATE TABLE printed on MariaDB 10.6.28 and 11.4.13.
    [Test]
    public void IntervalWithStartsAndAuto()
    {
        var options = MariaDbSystemTimeOptions.Read(") ENGINE=InnoDB WITH SYSTEM VERSIONING\n PARTITION BY SYSTEM_TIME INTERVAL 1 MONTH STARTS TIMESTAMP'2026-10-18 00:00:00' AUTO\nPARTITIONS 2");
        Assert.Multiple(() =>
        {
            Assert.That(options.Interval, Is.EqualTo("1 MONTH"));
            Assert.That(options.StartsAt, Is.EqualTo("2026-10-18 00:00:00"));
            Assert.That(options.Limit, Is.Null);
            Assert.That(options.Auto, Is.True);
        });
    }

    [Test]
    public void LimitWithAList()
    {
        var options = MariaDbSystemTimeOptions.Read(" PARTITION BY SYSTEM_TIME LIMIT 1000\n(PARTITION `h1` HISTORY ENGINE = InnoDB,\n PARTITION `cur` CURRENT ENGINE = InnoDB)");
        Assert.Multiple(() =>
        {
            Assert.That(options.Limit, Is.EqualTo(1000));
            Assert.That(options.Interval, Is.Null);
            Assert.That(options.Auto, Is.Null);
        });
    }

    [Test]
    public void IntervalWithoutAuto() =>
        Assert.That(MariaDbSystemTimeOptions.Read(" PARTITION BY SYSTEM_TIME INTERVAL 7 DAY STARTS TIMESTAMP'2026-01-01 00:00:00'\nPARTITIONS 2").Auto, Is.Null);

    [TestCase(" PARTITION BY SYSTEM_TIME \nPARTITIONS 2")]
    [TestCase(" PARTITION BY HASH (`id`)\nPARTITIONS 2")]
    [TestCase(null)]
    public void NoOptions_IsNull(string createTable) => Assert.That(MariaDbSystemTimeOptions.Read(createTable), Is.Null);
}
