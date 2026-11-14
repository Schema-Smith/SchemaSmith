// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Schema.Configuration;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class RunIdentityTests
{
    private static readonly Guid Id = Guid.Parse("5d2c1a0e-7f3b-4a55-9a0c-1c2f6f0e8b11");
    private static readonly RunHost Host = new("BUILD01", "svc-deploy");

    private static readonly Dictionary<string, string> GitHub = new()
    {
        ["GITHUB_ACTIONS"] = "true", ["GITHUB_WORKFLOW"] = "Deploy", ["GITHUB_RUN_ID"] = "99", ["GITHUB_RUN_ATTEMPT"] = "1",
        ["GITHUB_SERVER_URL"] = "https://github.com", ["GITHUB_REPOSITORY"] = "acme/db", ["GITHUB_ACTOR"] = "octocat",
        ["GITHUB_SHA"] = "ci-sha", ["GITHUB_REF_NAME"] = "ci-branch"
    };

    private static RunIdentity Build(Dictionary<string, string> settings, Dictionary<string, string> environment, GitSource git = null) =>
        RunIdentity.Build(Id, key => settings.GetValueOrDefault(key), CiEnvironmentTests.Env(environment), git,
            new PackageIdentity("sha256:00", 3), Host);

    [Test]
    public void ASettingBeatsTheEnvironment_FieldByField_KeepingTheProvidersOtherFields()
    {
        var identity = Build(new()
        {
            [SettingsKeys.Run.Commit] = "set-sha", [SettingsKeys.Run.Environment] = "production", [SettingsKeys.Run.Actor] = "release-bot"
        }, GitHub);

        Assert.That(identity.Source, Is.EqualTo(new RunSource("acme/db", "set-sha", "ci-branch", "setting")));
        Assert.That(identity.Ci, Is.EqualTo(new CiInfo("GitHubActions", "Deploy", "99", "1",
            "https://github.com/acme/db/actions/runs/99", "release-bot", "production")));
    }

    [Test]
    public void TheEnvironmentBeatsGit_AndGitFillsWhatTheEnvironmentLacks()
    {
        var environment = new Dictionary<string, string>(GitHub) { ["GITHUB_REF_NAME"] = "" };

        var identity = Build(new(), environment, new GitSource("git-sha", "git-branch"));

        Assert.That(identity.Source, Is.EqualTo(new RunSource("acme/db", "ci-sha", "git-branch", "ci")));
    }

    [Test]
    public void GitAlone_SuppliesTheSource()
    {
        var identity = Build(new(), new(), new GitSource("git-sha", "main"));

        Assert.That(identity.Source, Is.EqualTo(new RunSource(null, "git-sha", "main", "git")));
        Assert.That(identity.Ci, Is.Null);
    }

    [Test]
    public void SettingsWithNoCiSystem_GiveProviderOther()
    {
        var identity = Build(new()
        {
            [SettingsKeys.Run.Pipeline] = "Octopus: Billing", [SettingsKeys.Run.PipelineRunId] = "Releases-42",
            [SettingsKeys.Run.Url] = "https://octo.acme/app#/deployments/42", [SettingsKeys.Run.Label] = "2026.11.3"
        }, new());

        Assert.That(identity.Ci, Is.EqualTo(new CiInfo("Other", "Octopus: Billing", "Releases-42", null,
            "https://octo.acme/app#/deployments/42", null, null)));
        Assert.That(identity.Label, Is.EqualTo("2026.11.3"));
    }

    [Test]
    public void NothingFound_GivesNullSourceAndCi_ButAlwaysTheIdHostAndPackage()
    {
        var identity = Build(new(), new());

        Assert.That(identity.Source, Is.Null);
        Assert.That(identity.Ci, Is.Null);
        Assert.That(identity.Label, Is.Null);
        Assert.That(identity.Id, Is.EqualTo(Id));
        Assert.That(identity.Host, Is.EqualTo(Host));
        Assert.That(identity.Package, Is.EqualTo(new PackageIdentity("sha256:00", 3)));
    }

    [Test]
    public void AWhitespaceSetting_IsNoSetting()
    {
        var identity = Build(new() { [SettingsKeys.Run.Commit] = "  ", [SettingsKeys.Run.Label] = "" }, GitHub);

        Assert.That(identity.Source.Commit, Is.EqualTo("ci-sha"));
        Assert.That(identity.Label, Is.Null);
    }

    [TestCase("BUILD01", "ACME", "svc-deploy", "ACME\\svc-deploy")]
    [TestCase("BUILD01", "BUILD01", "svc-deploy", "svc-deploy")]
    [TestCase("linux-runner", "", "runner", "runner")]
    [TestCase("linux-runner", null, "runner", "runner")]
    public void Host_QualifiesTheUserWithItsDomain_OnlyWhenTheDomainIsNotTheMachine(string machine, string domain, string user, string expected)
    {
        Assert.That(RunHost.From(machine, domain, user), Is.EqualTo(new RunHost(machine, expected)));
    }

    [Test]
    public void TheRunSettings_AreKnownToSchemaQuench_SoTheyAreNotReportedAsUnrecognised()
    {
        foreach (var key in SettingsKeys.Run.All)
            Assert.That(SettingsContract.AcceptedKeys(SettingsTool.SchemaQuench), Does.Contain(key), key);
        Assert.That(SettingsKeys.Run.All, Has.Length.EqualTo(9));
    }
}
