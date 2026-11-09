// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class CiEnvironmentTests
{
    internal static IEnvironment Env(Dictionary<string, string> variables)
    {
        var env = Substitute.For<IEnvironment>();
        env.GetEnvironmentVariable(Arg.Any<string>()).Returns(call => variables.GetValueOrDefault((string)call[0]));
        return env;
    }

    [Test]
    public void GitHubActions()
    {
        var (ci, source) = CiEnvironment.Detect(Env(new()
        {
            ["GITHUB_ACTIONS"] = "true", ["GITHUB_WORKFLOW"] = "Deploy production", ["GITHUB_RUN_ID"] = "9912345678",
            ["GITHUB_RUN_ATTEMPT"] = "2", ["GITHUB_SERVER_URL"] = "https://github.com", ["GITHUB_REPOSITORY"] = "acme/billing-db",
            ["GITHUB_ACTOR"] = "octocat", ["GITHUB_SHA"] = "4f2a9c1e", ["GITHUB_REF_NAME"] = "main"
        }));

        Assert.That(ci, Is.EqualTo(new CiInfo("GitHubActions", "Deploy production", "9912345678", "2",
            "https://github.com/acme/billing-db/actions/runs/9912345678", "octocat", null)));
        Assert.That(source, Is.EqualTo(new CiSource("acme/billing-db", "4f2a9c1e", "main")));
    }

    // On a pull request GITHUB_REF_NAME is "<number>/merge"; the branch is the pull request's own.
    [Test]
    public void GitHubActions_OnAPullRequest_TheBranchIsThePullRequests()
    {
        var (_, source) = CiEnvironment.Detect(Env(new()
        {
            ["GITHUB_ACTIONS"] = "true", ["GITHUB_REF_NAME"] = "12/merge", ["GITHUB_HEAD_REF"] = "feature/orders", ["GITHUB_SHA"] = "abc"
        }));

        Assert.That(source.Branch, Is.EqualTo("feature/orders"));
    }

    [TestCase("https://dev.azure.com/acme/")]
    [TestCase("https://dev.azure.com/acme")]
    public void AzureDevOps_ComposesTheRunUrl_WithOrWithoutATrailingSlash(string collection)
    {
        var (ci, source) = CiEnvironment.Detect(Env(new()
        {
            ["TF_BUILD"] = "True", ["BUILD_DEFINITIONNAME"] = "billing-db CI", ["BUILD_BUILDID"] = "812",
            ["SYSTEM_JOBATTEMPT"] = "1", ["SYSTEM_TEAMFOUNDATIONCOLLECTIONURI"] = collection, ["SYSTEM_TEAMPROJECT"] = "Billing",
            ["BUILD_REQUESTEDFOR"] = "Ada Lovelace", ["BUILD_SOURCEVERSION"] = "abc123", ["BUILD_SOURCEBRANCHNAME"] = "main",
            ["BUILD_REPOSITORY_NAME"] = "billing-db", ["ENVIRONMENT_NAME"] = "production"
        }));

        Assert.That(ci, Is.EqualTo(new CiInfo("AzureDevOps", "billing-db CI", "812", "1",
            "https://dev.azure.com/acme/Billing/_build/results?buildId=812", "Ada Lovelace", "production")));
        Assert.That(source, Is.EqualTo(new CiSource("billing-db", "abc123", "main")));
    }

    [Test]
    public void AzureDevOps_ClassicRelease_TakesTheReleaseEnvironment_AndIsRecognisedInAnyCase()
    {
        var (ci, _) = CiEnvironment.Detect(Env(new() { ["TF_BUILD"] = "true", ["RELEASE_ENVIRONMENTNAME"] = "staging" }));

        Assert.That(ci.Provider, Is.EqualTo("AzureDevOps"));
        Assert.That(ci.Environment, Is.EqualTo("staging"));
    }

    [Test]
    public void GitLab()
    {
        var (ci, source) = CiEnvironment.Detect(Env(new()
        {
            ["GITLAB_CI"] = "true", ["CI_PROJECT_PATH"] = "acme/billing-db", ["CI_PIPELINE_ID"] = "4411",
            ["CI_PIPELINE_URL"] = "https://gitlab.com/acme/billing-db/-/pipelines/4411", ["GITLAB_USER_LOGIN"] = "ada",
            ["CI_COMMIT_SHA"] = "def456", ["CI_COMMIT_REF_NAME"] = "release", ["CI_ENVIRONMENT_NAME"] = "production"
        }));

        Assert.That(ci, Is.EqualTo(new CiInfo("GitLab", "acme/billing-db", "4411", null,
            "https://gitlab.com/acme/billing-db/-/pipelines/4411", "ada", "production")));
        Assert.That(source, Is.EqualTo(new CiSource("acme/billing-db", "def456", "release")));
    }

    [TestCase("JENKINS_URL", "https://ci.acme.local/")]
    [TestCase("JENKINS_HOME", "/var/jenkins_home")]
    public void Jenkins_RecognisedByUrlOrHome_AndTheRemotePrefixLeavesTheBranch(string marker, string value)
    {
        var (ci, source) = CiEnvironment.Detect(Env(new()
        {
            [marker] = value, ["JOB_NAME"] = "billing/deploy", ["BUILD_NUMBER"] = "153",
            ["BUILD_URL"] = "https://ci.acme.local/job/billing/job/deploy/153/", ["GIT_COMMIT"] = "fed789", ["GIT_BRANCH"] = "origin/main"
        }));

        Assert.That(ci, Is.EqualTo(new CiInfo("Jenkins", "billing/deploy", "153", null,
            "https://ci.acme.local/job/billing/job/deploy/153/", null, null)));
        Assert.That(source, Is.EqualTo(new CiSource(null, "fed789", "main")));
    }

    [Test]
    public void NoProvider_GivesNulls()
    {
        Assert.That(CiEnvironment.Detect(Env(new() { ["PATH"] = "/usr/bin" })), Is.EqualTo(((CiInfo)null, (CiSource)null)));
    }

    [Test]
    public void EmptyVariables_ReadAsNull()
    {
        var (ci, source) = CiEnvironment.Detect(Env(new()
        {
            ["GITHUB_ACTIONS"] = "true", ["GITHUB_RUN_ID"] = "7", ["GITHUB_RUN_ATTEMPT"] = "", ["GITHUB_ACTOR"] = "  ",
            ["GITHUB_SHA"] = "", ["GITHUB_REF_NAME"] = "", ["GITHUB_REPOSITORY"] = ""
        }));

        Assert.That(ci.RunAttempt, Is.Null);
        Assert.That(ci.Actor, Is.Null);
        Assert.That(ci.Url, Is.Null, "no URL is composed from missing parts");
        Assert.That(source, Is.Null, "a provider with nothing about the source gives no source");
    }
}
