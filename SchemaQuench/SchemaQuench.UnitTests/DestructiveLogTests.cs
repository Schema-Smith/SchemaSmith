// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

namespace SchemaQuench.UnitTests;

public class DestructiveLogTests
{
    [TestCase(1L, "column dbo.T.C changed INT -> TINYINT, which changes its values without an error (about 1 row)")]
    [TestCase(1200L, "column dbo.T.C changed INT -> TINYINT, which changes its values without an error (about 1,200 rows)")]
    [TestCase(0L, "column dbo.T.C changed INT -> TINYINT, which changes its values without an error (about 0 rows)")]
    public void Describe_CountsRowsInTheRightNumber(long rows, string expected) =>
        Assert.That(DestructiveLog.Describe(new ChangeAuditRow("type change", "dbo.T.C", DestructiveLog.Action, "warned", Detail: "INT -> TINYINT", EstimatedRows: rows)),
            Is.EqualTo(expected));
}
