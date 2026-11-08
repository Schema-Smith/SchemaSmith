// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Schema.Domain.Settings;
using SchemaQuench.Settings;

namespace SchemaQuench.UnitTests.Settings;

[TestFixture]
public class SettingsStepTests
{
    private sealed class EngineRefusedException(string message) : DbException(message);

    private ISettingDescriptor _descriptor;
    private ChangeAuditCapture _audit;
    private List<(SettingLevel Level, string Message)> _log;

    [SetUp]
    public void SetUp()
    {
        _descriptor = Substitute.For<ISettingDescriptor>();
        _descriptor.PersistsAcrossRestart.Returns(true);
        _descriptor.Equivalent(Arg.Any<string>(), Arg.Any<SettingReading>()).Returns(ci => ci.ArgAt<string>(0) == ci.ArgAt<SettingReading>(1).Configured);
        _audit = new ChangeAuditCapture();
        _log = [];
    }

    private static Setting S(bool fix, DriftLevel onDrift, string value = "8", string name = "maxdop") =>
        new() { Kind = "Option", Name = name, Value = value, Fix = fix, OnDrift = onDrift };

    private void Reads(params string[] values)
    {
        var queue = new Queue<string>(values);
        _descriptor.Read(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => new SettingReading(true, queue.Count > 1 ? queue.Dequeue() : queue.Peek(), null, false, null));
    }

    private SettingsStepResult Run(bool whatIf, params Setting[] settings) =>
        new SettingsStep(_ => _descriptor).Run(Substitute.For<IDbCommand>(), new SettingsFile { Scope = SettingScope.Database, Settings = settings.ToList() },
            "db", whatIf, _ => true, _audit, new ChangeTarget("srv", "db"), (level, message) => _log.Add((level, message)));

    private string[] AuditActions() => _audit.Snapshot().Select(r => $"{r.ObjectType} {r.ObjectName} {r.Action}").ToArray();

    [Test]
    public void ReportErrorDrift_StopsBeforeAnyApply()
    {
        Reads("4");

        var result = Run(false, S(true, DriftLevel.Warn, name: "a"), S(false, DriftLevel.Error, name: "b"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Stopped, Is.True);
            Assert.That(result.StopMessage, Does.Contain("Option 'b' is 4, declared 8 (not fixed)"));
            _descriptor.DidNotReceive().Apply(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
            Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option a wouldChange", "DatabaseSetting Option b drift" }),
                "every evaluation is still reported; the fix that did not run reads as one that would have");
        });
    }

    [Test]
    public void WhatIf_AppliesNothing_AndReportsWouldChange()
    {
        Reads("4");

        var result = Run(true, S(true, DriftLevel.Error));

        Assert.Multiple(() =>
        {
            Assert.That(result.Stopped, Is.False);
            _descriptor.DidNotReceive().Apply(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
            Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop wouldChange" }));
            Assert.That(_log.Single().Message, Is.EqualTo("Database setting Option 'maxdop' would change: 4 -> 8"));
        });
    }

    [Test]
    public void WhatIf_ReportErrorDrift_SaysTheRunWouldStop_WithoutStopping()
    {
        Reads("4");

        var result = Run(true, S(false, DriftLevel.Error));

        Assert.That(result.Stopped, Is.False);
        Assert.That(_log.Select(l => l.Message), Has.Some.StartsWith("[WhatIf] would stop here: Database setting Option 'maxdop' is 4"));
    }

    [Test]
    public void ApplyThrows_GradesFixFailed_WithTheEngineMessage()
    {
        Reads("4");
        _descriptor.When(d => d.Apply(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new EngineRefusedException("permission denied"));

        var result = Run(false, S(true, DriftLevel.Error));

        Assert.Multiple(() =>
        {
            Assert.That(result.Stopped, Is.True, "an enforce-error failure stops the deploy after the step");
            Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop fixFailed" }));
            Assert.That(_log.Single(), Is.EqualTo((SettingLevel.Error, "Database setting Option 'maxdop' fix failed: permission denied")));
        });
    }

    [Test]
    public void ASqlServerErrorReportedAsAMessage_IsAFailedFix_NotAFailedDeploy()
    {
        Reads("4");
        _descriptor.When(d => d.Apply(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new SqlServerErrorException(15048, "Valid values of the database compatibility level are 100 or 110."));

        var result = Run(false, S(true, DriftLevel.Warn));

        Assert.That(result.Stopped, Is.False);
        Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop fixFailed" }));
    }

    [Test]
    public void ApplySucceeds_ButTheServerKeepsItsValue_IsUnfixable_NotChanged()
    {
        Reads("4", "4");

        Run(false, S(true, DriftLevel.Warn));

        Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop unfixable" }));
        Assert.That(_log.Single().Message, Does.EndWith("cannot be fixed on this server: the server kept 4"));
    }

    [Test]
    public void AFixThatTook_IsChanged_FromTheReRead()
    {
        Reads("4", "8");

        var result = Run(false, S(true, DriftLevel.Error));

        Assert.That(result.Stopped, Is.False);
        Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop changed" }));
        Assert.That(_log.Single(), Is.EqualTo((SettingLevel.Info, "Database setting Option 'maxdop': 4 -> 8")));
    }

    [Test]
    public void EnforceErrorPendingRestart_StopsAfterTheStep()
    {
        var queue = new Queue<SettingReading>([new(true, "4", "4", false, null), new(true, "8", "4", true, null)]);
        _descriptor.Read(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>()).Returns(_ => queue.Dequeue());

        var result = Run(false, S(true, DriftLevel.Error));

        Assert.That(result.Stopped, Is.True);
        Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop pendingRestart" }));
        Assert.That(result.StopMessage, Does.Contain("takes effect after a restart (running value 4)"));
    }

    [Test]
    public void MatchingSetting_NoLog_NoAudit_NoApply()
    {
        Reads("8");

        var result = Run(false, S(true, DriftLevel.Error));

        Assert.Multiple(() =>
        {
            Assert.That(result.Stopped, Is.False);
            Assert.That(_log, Is.Empty);
            Assert.That(AuditActions(), Is.Empty);
            _descriptor.DidNotReceive().Apply(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        });
    }

    [Test]
    public void AReadTheAccountCannotMake_IsUnfixable_AndTheRunGoesOn()
    {
        _descriptor.Read(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>()).Throws(new EngineRefusedException("SELECT denied"));

        var result = Run(false, S(true, DriftLevel.Warn));

        Assert.That(result.Stopped, Is.False);
        Assert.That(_log.Single(), Is.EqualTo((SettingLevel.Warning, "Database setting Option 'maxdop' cannot be fixed on this server: SELECT denied")));
    }

    [Test]
    public void ANameTheServerDoesNotHave_IsUnfixable_WithTheReason()
    {
        _descriptor.Read(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>()).Returns(SettingReading.Missing("no such option"));

        Run(false, S(true, DriftLevel.Ignore));

        Assert.That(_log.Single(), Is.EqualTo((SettingLevel.Info, "Database setting Option 'maxdop' cannot be fixed on this server: no such option")));
    }

    [Test]
    public void TwoVariantsThatBothApply_StopTheStep()
    {
        var a = S(true, DriftLevel.Warn);
        a.ShouldApplyExpression = "x"; a.VariantName = "A";
        var b = S(true, DriftLevel.Warn, "0");
        b.ShouldApplyExpression = "y"; b.VariantName = "B";

        var result = Run(false, a, b);

        Assert.That(result.Stopped, Is.True);
        Assert.That(result.StopMessage, Is.EqualTo("Option 'maxdop': variants 'A' and 'B' both apply"));
        _descriptor.DidNotReceive().Read(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void AChangeTheServerCannotTakeFromHere_IsUnfixable()
    {
        Reads("4");
        _descriptor.When(d => d.Apply(Arg.Any<IDbCommand>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new SettingUnfixableException("read-only variable; set it in the server's option file"));

        Run(false, S(true, DriftLevel.Warn));

        Assert.That(AuditActions(), Is.EqualTo(new[] { "DatabaseSetting Option maxdop unfixable" }));
    }

    [Test]
    public void ANonPersistentFix_SaysHowToKeepIt()
    {
        Reads("4", "8");
        _descriptor.PersistsAcrossRestart.Returns(false);
        _descriptor.RevertAdvice("maxdop").Returns("add -T1 to keep it");

        Run(false, S(true, DriftLevel.Error));

        Assert.That(_log.Single().Message, Does.EndWith("reverts at the next restart (add -T1 to keep it)"));
    }

    [Test]
    public void ANonPersistentFix_IsChangedWithARevertWarning()
    {
        Reads("4", "8");
        _descriptor.PersistsAcrossRestart.Returns(false);

        Run(false, S(true, DriftLevel.Error));

        Assert.That(_log.Single().Level, Is.EqualTo(SettingLevel.Warning));
        Assert.That(_log.Single().Message, Does.Contain("reverts at the next restart"));
    }
}
