// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Domain.Settings;

namespace SchemaQuench.Settings;

/// <summary>The D12 policy table: the level grades what is still wrong once the deploy has done its part, and each outcome
/// has one wording, so the three ways a fix can fall short read differently.</summary>
public static class SettingsPolicy
{
    public static SettingGrade Grade(SettingEvaluation e)
    {
        if (e.Outcome == SettingOutcome.Matches) return null;
        var s = e.Setting;
        var fix = s.Fix == true;
        var residual = s.OnDrift switch { DriftLevel.Error => SettingLevel.Error, DriftLevel.Ignore => SettingLevel.Info, _ => SettingLevel.Warning };
        var prefix = $"{(e.Scope == SettingScope.Server ? "Server" : "Database")} setting {s.Display}";

        return (fix, e.Outcome) switch
        {
            (false, SettingOutcome.Drift) => new(residual, "drift", $"{prefix} is {e.Current}, declared {s.Value} (not fixed)"),
            (_, SettingOutcome.Unfixable) => new(residual, "unfixable", $"{prefix} cannot be fixed on this server: {e.Detail}"),
            (true, SettingOutcome.FixedActive) => new(SettingLevel.Info, "changed", $"{prefix}: {e.Current} -> {s.Value}"),
            (true, SettingOutcome.FixedReverts) => new(SettingLevel.Warning, "changed",
                $"{prefix}: {e.Current} -> {s.Value}; the value reverts at the next restart" + (e.Detail == null ? "" : $" ({e.Detail})")),
            (true, SettingOutcome.FixedPendingRestart) => new(residual, "pendingRestart",
                $"{prefix} set to {s.Value}; takes effect after a restart (running value {e.Running})"),
            (true, SettingOutcome.FixFailed) => new(residual, "fixFailed", $"{prefix} fix failed: {e.Detail}"),
            (true, SettingOutcome.WouldChange) => new(SettingLevel.Info, "wouldChange", $"{prefix} would change: {e.Current} -> {s.Value}"),
            _ => throw new InvalidOperationException($"{s.Display}: outcome {e.Outcome} cannot arise with Fix {fix}.")
        };
    }

    // Report-only Error drift stops the step before anything changes; an enforce-error failure stops the deploy after it.
    public static bool StopsBeforeChanges(SettingEvaluation e) =>
        e.Setting.Fix == false && e.Setting.OnDrift == DriftLevel.Error && e.Outcome is SettingOutcome.Drift or SettingOutcome.Unfixable;
}
