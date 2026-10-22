// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

/// <summary>
/// OnlineApply and OnlineApplyMaxDuration as the procedures receive them. A value the deploy cannot act on stops it with
/// the accepted values, rather than reaching the procedures as something they read as Off.
/// </summary>
[TestFixture]
public class OnlineApplySettingTests
{
    [TestCase(null, "OFF")]
    [TestCase("", "OFF")]
    [TestCase("  ", "OFF")]
    [TestCase("off", "OFF")]
    [TestCase("Prefer", "PREFER")]
    [TestCase(" require ", "REQUIRE")]
    public void OnlineApply_IsUpperCased_AndUnsetIsOff(string configured, string expected) =>
        Assert.That(DatabaseQuench.ResolveOnlineApply(configured), Is.EqualTo(expected));

    [Test]
    public void OnlineApply_AnythingElse_IsRefusedWithTheAcceptedValues()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseQuench.ResolveOnlineApply("Online"));
        Assert.That(ex!.Message, Is.EqualTo("OnlineApply must be Off, Prefer or Require, not 'Online'."));
    }

    [TestCase(null, "NULL")]
    [TestCase("", "NULL")]
    [TestCase("1", "1")]
    [TestCase(" 90 ", "90")]
    [TestCase("10080", "10080")]
    public void OnlineApplyMaxDuration_IsWholeMinutes_AndUnsetIsNull(string configured, string expected) =>
        Assert.That(DatabaseQuench.ResolveOnlineApplyMaxDuration(configured), Is.EqualTo(expected));

    [TestCase("0")]
    [TestCase("10081")]
    [TestCase("-5")]
    [TestCase("1.5")]
    [TestCase("ten")]
    public void OnlineApplyMaxDuration_OutsideOneMinuteToAWeek_IsRefused(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseQuench.ResolveOnlineApplyMaxDuration(configured));
        Assert.That(ex!.Message, Does.StartWith("OnlineApplyMaxDuration must be a whole number of minutes from 1 to 10080"));
    }
}
