// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Schema.Domain.Settings;
using SchemaQuench.Settings;

namespace SchemaQuench.UnitTests.Settings;

// The policy table in the D12 design, §2: the level grades what is still wrong once the deploy has done its part.
[TestFixture]
public class SettingsPolicyTests
{
    private static SettingEvaluation E(bool fix, DriftLevel onDrift, SettingOutcome outcome, SettingScope scope = SettingScope.Server,
        string current = "4", string running = "4", string detail = null) =>
        new(new Setting { Kind = "Configuration", Name = "max degree of parallelism", Value = "8", Fix = fix, OnDrift = onDrift },
            scope, outcome, current, running, detail);

    private static IEnumerable<TestCaseData> Cells()
    {
        // Report-only: drift, and a setting the server cannot read or does not have, graded by OnDrift.
        foreach (var outcome in new[] { SettingOutcome.Drift, SettingOutcome.Unfixable })
        {
            var action = outcome == SettingOutcome.Drift ? "drift" : "unfixable";
            yield return new TestCaseData(false, DriftLevel.Warn, outcome, SettingLevel.Warning, action);
            yield return new TestCaseData(false, DriftLevel.Error, outcome, SettingLevel.Error, action);
            yield return new TestCaseData(false, DriftLevel.Ignore, outcome, SettingLevel.Info, action);
        }

        foreach (var onDrift in new[] { DriftLevel.Warn, DriftLevel.Error, DriftLevel.Ignore })
        {
            var residual = onDrift switch { DriftLevel.Warn => SettingLevel.Warning, DriftLevel.Error => SettingLevel.Error, _ => SettingLevel.Info };
            yield return new TestCaseData(true, onDrift, SettingOutcome.FixedActive, SettingLevel.Info, "changed");
            // Active but not surviving a restart: a successful fix with a revert warning under every enforce policy.
            yield return new TestCaseData(true, onDrift, SettingOutcome.FixedReverts, SettingLevel.Warning, "changed");
            yield return new TestCaseData(true, onDrift, SettingOutcome.FixedPendingRestart, residual, "pendingRestart");
            yield return new TestCaseData(true, onDrift, SettingOutcome.FixFailed, residual, "fixFailed");
            yield return new TestCaseData(true, onDrift, SettingOutcome.Unfixable, residual, "unfixable");
            yield return new TestCaseData(true, onDrift, SettingOutcome.WouldChange, SettingLevel.Info, "wouldChange");
        }
    }

    [TestCaseSource(nameof(Cells))]
    public void EveryCell_GradesAndAuditsAsTheTableSays(bool fix, DriftLevel onDrift, SettingOutcome outcome, SettingLevel level, string action)
    {
        var grade = SettingsPolicy.Grade(E(fix, onDrift, outcome));

        Assert.That((grade.Level, grade.AuditAction), Is.EqualTo((level, action)));
    }

    [Test]
    public void Matches_IsSilent()
    {
        Assert.That(SettingsPolicy.Grade(E(true, DriftLevel.Error, SettingOutcome.Matches)), Is.Null);
    }

    [TestCase(false, SettingOutcome.FixedActive)]
    [TestCase(true, SettingOutcome.Drift)]
    public void ACombinationTheTableRulesOut_Throws(bool fix, SettingOutcome outcome)
    {
        Assert.That(() => SettingsPolicy.Grade(E(fix, DriftLevel.Warn, outcome)), Throws.InstanceOf<InvalidOperationException>());
    }

    [TestCase(SettingOutcome.FixedActive, null, "Server setting Configuration 'max degree of parallelism': 4 -> 8")]
    [TestCase(SettingOutcome.FixedPendingRestart, null, "Server setting Configuration 'max degree of parallelism' set to 8; takes effect after a restart (running value 4)")]
    [TestCase(SettingOutcome.FixFailed, "permission denied", "Server setting Configuration 'max degree of parallelism' fix failed: permission denied")]
    [TestCase(SettingOutcome.Unfixable, "no such setting on this version", "Server setting Configuration 'max degree of parallelism' cannot be fixed on this server: no such setting on this version")]
    [TestCase(SettingOutcome.FixedReverts, null, "Server setting Configuration 'max degree of parallelism': 4 -> 8; the value reverts at the next restart")]
    [TestCase(SettingOutcome.WouldChange, null, "Server setting Configuration 'max degree of parallelism' would change: 4 -> 8")]
    public void EnforceMessages(SettingOutcome outcome, string detail, string message)
    {
        Assert.That(SettingsPolicy.Grade(E(true, DriftLevel.Warn, outcome, detail: detail)).Message, Is.EqualTo(message));
    }

    [Test]
    public void ReportOnlyDrift_Message()
    {
        Assert.That(SettingsPolicy.Grade(E(false, DriftLevel.Warn, SettingOutcome.Drift, SettingScope.Database)).Message,
            Is.EqualTo("Database setting Configuration 'max degree of parallelism' is 4, declared 8 (not fixed)"));
    }

    [Test]
    public void ARevertWithAReason_SaysWhy()
    {
        Assert.That(SettingsPolicy.Grade(E(true, DriftLevel.Warn, SettingOutcome.FixedReverts, detail: "add -T1117 to the startup parameters to keep it")).Message,
            Does.EndWith("; the value reverts at the next restart (add -T1117 to the startup parameters to keep it)"));
    }

    [TestCase(false, DriftLevel.Error, SettingOutcome.Drift, true)]
    [TestCase(false, DriftLevel.Error, SettingOutcome.Unfixable, true)]
    [TestCase(false, DriftLevel.Warn, SettingOutcome.Drift, false)]
    [TestCase(true, DriftLevel.Error, SettingOutcome.FixFailed, false)]
    [TestCase(true, DriftLevel.Error, SettingOutcome.Matches, false)]
    public void OnlyReportOnlyErrors_StopBeforeChanges(bool fix, DriftLevel onDrift, SettingOutcome outcome, bool stops)
    {
        Assert.That(SettingsPolicy.StopsBeforeChanges(E(fix, onDrift, outcome)), Is.EqualTo(stops));
    }
}
