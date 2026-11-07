// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Schema.Domain.Settings;
using SchemaQuench.Settings;

namespace SchemaQuench.UnitTests.Settings;

[TestFixture]
public class SettingVariantsTests
{
    private static Setting S(string name, string value, string gate = null, string variant = null, string kind = "Configuration") =>
        new() { Kind = kind, Name = name, Value = value, Fix = true, OnDrift = DriftLevel.Warn, ShouldApplyExpression = gate, VariantName = variant };

    private static bool Gate(string expression) => expression == "yes";

    [Test]
    public void UngatedSettings_AreAllActive()
    {
        var active = SettingVariants.Select([S("a", "1"), S("b", "2")], Gate, out var error);

        Assert.That(error, Is.Null);
        Assert.That(active.Select(s => s.Name), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void TheVariantWhoseGateHolds_IsTheOneManaged()
    {
        var active = SettingVariants.Select([S("maxdop", "8", "yes", "OLTP"), S("maxdop", "0", "no", "DW")], Gate, out var error);

        Assert.That(error, Is.Null);
        Assert.That(active.Single().VariantName, Is.EqualTo("OLTP"));
    }

    [Test]
    public void NoGateHolds_TheSettingIsNotManagedHere()
    {
        Assert.That(SettingVariants.Select([S("maxdop", "8", "no", "OLTP")], Gate, out _), Is.Empty);
    }

    [Test]
    public void TwoVariantsThatBothApply_AreAnError_NotTheLastOneWins()
    {
        var active = SettingVariants.Select([S("maxdop", "8", "yes", "OLTP"), S("MAXDOP", "0", "yes", "DW"), S("other", "1")], Gate, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(active, Is.Empty);
            Assert.That(error, Is.EqualTo("Configuration 'maxdop': variants 'OLTP' and 'DW' both apply"));
        });
    }

    [Test]
    public void TheSameNameUnderAnotherKind_IsADifferentSetting()
    {
        var active = SettingVariants.Select([S("MAXDOP", "8", kind: "Option"), S("MAXDOP", "4", kind: "ScopedConfiguration")], Gate, out var error);

        Assert.That(error, Is.Null);
        Assert.That(active, Has.Count.EqualTo(2));
    }

    [Test]
    public void AGateIsEvaluatedOnce()
    {
        var calls = 0;
        SettingVariants.Select([S("a", "1", "yes", "x")], e => { calls++; return true; }, out _);
        Assert.That(calls, Is.EqualTo(1));
    }
}
