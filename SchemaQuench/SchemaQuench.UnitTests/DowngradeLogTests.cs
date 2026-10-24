// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;

namespace SchemaQuench.UnitTests;

// PostgreSQL recorded a feature it skipped under the warn policy only as an audit row, so nothing on the console said so;
// SQL Server, MySQL and MariaDB print a line at the step that skips it.
[TestFixture]
public class DowngradeLogTests
{
    private static readonly ChangeAuditRow[] Rows =
    [
        new("VIRTUAL generated column (PG18)", "public.orders.total", "downgraded"),
        new("table", "public.orders", "created"),
        new("NULLS NOT DISTINCT (PG15)", "public.orders.ux_code", "downgraded")
    ];

    [Test]
    public void PostgreSql_SaysWhatItSkipped()
    {
        Assert.That(DowngradeLog.Lines(Platform.PostgreSQL, Rows), Is.EqualTo(new[]
        {
            "  Skipping VIRTUAL generated column (PG18) on public.orders.total (downgraded)",
            "  Skipping NULLS NOT DISTINCT (PG15) on public.orders.ux_code (downgraded)"
        }));
    }

    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void AnEngineThatSaysSoAtTheStep_IsNotToldTwice(Platform platform)
    {
        Assert.That(DowngradeLog.Lines(platform, Rows), Is.Empty);
    }
}
