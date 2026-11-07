// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain.Settings;

namespace SchemaQuench.Settings;

public enum SettingOutcome { Matches, Drift, FixedActive, FixedPendingRestart, FixedReverts, FixFailed, Unfixable, WouldChange }

public enum SettingLevel { Info, Warning, Error }

// Current is the value compared (configured); Running is the value in effect when they differ. Detail carries the engine's
// error for a failed fix, or the reason a setting cannot be fixed.
public sealed record SettingEvaluation(Setting Setting, SettingScope Scope, SettingOutcome Outcome, string Current, string Running, string Detail);

public sealed record SettingGrade(SettingLevel Level, string AuditAction, string Message);
