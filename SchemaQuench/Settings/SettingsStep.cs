// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Schema.Domain.Settings;
using Schema.Settings;

namespace SchemaQuench.Settings;

public sealed record SettingsStepResult(IReadOnlyList<SettingEvaluation> Evaluations, bool Stopped, string StopMessage);

/// <summary>
/// One settings file against one server or database: read every declared setting, stop before changing anything when a
/// report-only Error setting has drifted, apply the fixes, re-read, and grade each outcome from what the server now says.
/// Matching settings are silent. Nothing is written under WhatIf.
/// </summary>
public sealed class SettingsStep(Func<string, ISettingDescriptor> descriptorFor)
{
    private sealed record Pending(Setting Setting, ISettingDescriptor Descriptor, SettingReading Before);

    public SettingsStepResult Run(IDbCommand cmd, SettingsFile file, string database, bool whatIf, Func<string, bool> gate,
        ChangeAuditCapture audit, ChangeTarget target, Action<SettingLevel, string> log)
    {
        var active = SettingVariants.Select(file.Settings, gate, out var variantError);
        if (variantError != null)
        {
            log(SettingLevel.Error, variantError);
            return new SettingsStepResult([], !whatIf, variantError);
        }

        var evaluations = new List<SettingEvaluation>();
        var toFix = new List<Pending>();
        foreach (var setting in active)
        {
            var descriptor = descriptorFor(setting.Kind);
            var before = SafeRead(descriptor, cmd, setting.Name, database);
            var outcome = Initial(setting, descriptor, before);
            if (outcome == SettingOutcome.FixedActive)
            {
                toFix.Add(new Pending(setting, descriptor, before));
                outcome = SettingOutcome.WouldChange;
            }
            evaluations.Add(new SettingEvaluation(setting, file.Scope, outcome, before.Configured, before.Running, before.UnfixableReason));
        }

        var stopper = evaluations.FirstOrDefault(SettingsPolicy.StopsBeforeChanges);
        if (stopper != null || whatIf)
        {
            Report(evaluations, audit, target, log);
            if (stopper == null) return new SettingsStepResult(evaluations, false, null);
            var message = SettingsPolicy.Grade(stopper).Message;
            if (whatIf) log(SettingLevel.Error, $"[WhatIf] would stop here: {message}");
            return new SettingsStepResult(evaluations, !whatIf, message);
        }

        // In the descriptor's order (one change can undo another), and each graded from what the server says after all of them.
        var failed = new Dictionary<Setting, SettingEvaluation>();
        foreach (var pending in toFix.OrderBy(p => p.Descriptor.ApplyOrder(p.Setting.Name)))
            if (TryApply(cmd, pending, file.Scope, database) is { } failure) failed[pending.Setting] = failure;
        foreach (var pending in toFix)
        {
            var index = evaluations.FindIndex(e => ReferenceEquals(e.Setting, pending.Setting));
            evaluations[index] = failed.TryGetValue(pending.Setting, out var f) ? f : Grade(cmd, pending, file.Scope, database);
        }

        Report(evaluations, audit, target, log);
        var error = evaluations.Select(SettingsPolicy.Grade).FirstOrDefault(g => g?.Level == SettingLevel.Error);
        return new SettingsStepResult(evaluations, error != null, error?.Message);
    }

    // FixedActive here only marks "drifted and Fix is on", which Run turns into an apply.
    private static SettingOutcome Initial(Setting setting, ISettingDescriptor descriptor, SettingReading reading)
    {
        if (!reading.Exists) return SettingOutcome.Unfixable;
        if (descriptor.Equivalent(setting.Value, reading)) return SettingOutcome.Matches;
        if (reading.UnfixableReason != null) return SettingOutcome.Unfixable;
        return setting.Fix == true ? SettingOutcome.FixedActive : SettingOutcome.Drift;
    }

    // Null when the change was sent; the failure otherwise.
    private static SettingEvaluation TryApply(IDbCommand cmd, Pending p, SettingScope scope, string database)
    {
        try
        {
            p.Descriptor.Apply(cmd, p.Setting.Name, p.Setting.Value, database);
            return null;
        }
        catch (SettingUnfixableException e)
        {
            return new SettingEvaluation(p.Setting, scope, SettingOutcome.Unfixable, p.Before.Configured, p.Before.Running, e.Message);
        }
        catch (DbException e)
        {
            return Failed(p, scope, e);
        }
        catch (SettingValueException e)
        {
            return Failed(p, scope, e);
        }
        // SQL Server reports an error through InfoMessage on SchemaQuench's connections, not by throwing.
        catch (SqlServerErrorException e)
        {
            return Failed(p, scope, e);
        }
    }

    // Graded from what the server now reports, never from the absence of an error.
    private static SettingEvaluation Grade(IDbCommand cmd, Pending p, SettingScope scope, string database)
    {
        var after = SafeRead(p.Descriptor, cmd, p.Setting.Name, database);
        var took = after.Exists && p.Descriptor.Equivalent(p.Setting.Value, after);
        SettingOutcome outcome;
        string detail = null;
        if (after.PendingRestart == true) outcome = SettingOutcome.FixedPendingRestart;
        else if (!took)
        {
            outcome = SettingOutcome.Unfixable;
            detail = after.Exists ? $"the server kept {after.Configured}" : after.UnfixableReason;
        }
        else if (p.Descriptor.PersistsAcrossRestart) outcome = SettingOutcome.FixedActive;
        else
        {
            outcome = SettingOutcome.FixedReverts;
            detail = p.Descriptor.RevertAdvice(p.Setting.Name);
        }
        return new SettingEvaluation(p.Setting, scope, outcome, p.Before.Configured, after.Running ?? p.Before.Running, detail ?? after.UnfixableReason);
    }

    private static SettingEvaluation Failed(Pending p, SettingScope scope, Exception e) =>
        new(p.Setting, scope, SettingOutcome.FixFailed, p.Before.Configured, p.Before.Running, e.Message);

    private static SettingReading SafeRead(ISettingDescriptor descriptor, IDbCommand cmd, string name, string database)
    {
        try
        {
            return descriptor.Read(cmd, name, database);
        }
        catch (DbException e)
        {
            return SettingReading.Missing(e.Message);
        }
        catch (SqlServerErrorException e)
        {
            return SettingReading.Missing(e.Message);
        }
    }

    private static void Report(IEnumerable<SettingEvaluation> evaluations, ChangeAuditCapture audit, ChangeTarget target, Action<SettingLevel, string> log)
    {
        foreach (var e in evaluations)
        {
            var grade = SettingsPolicy.Grade(e);
            if (grade == null) continue;
            log(grade.Level, grade.Message);
            audit?.Record(e.Scope == SettingScope.Server ? "ServerSetting" : "DatabaseSetting", $"{e.Setting.Kind} {e.Setting.Name}", grade.AuditAction, target: target);
        }
    }
}
