// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Isolators;

namespace SchemaQuench.Reporting;

public sealed record CiInfo(string Provider, string Pipeline, string RunId, string RunAttempt, string Url, string Actor, string Environment);

public sealed record CiSource(string Repository, string Commit, string Branch);

/// <summary>
/// Which pipeline run this process belongs to, read from the variables each CI system sets for every job. An empty
/// variable reads as absent. Octopus Deploy is not here because it does not put its variables in the environment; it
/// supplies the same facts through the Run* settings.
/// </summary>
public static class CiEnvironment
{
    public static (CiInfo Ci, CiSource Source) Detect(IEnvironment environment)
    {
        string Get(string name)
        {
            var value = environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        bool IsTrue(string name) => string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);

        if (IsTrue("GITHUB_ACTIONS"))
            return (new CiInfo("GitHubActions", Get("GITHUB_WORKFLOW"), Get("GITHUB_RUN_ID"), Get("GITHUB_RUN_ATTEMPT"),
                    Compose(Get("GITHUB_SERVER_URL"), Get("GITHUB_REPOSITORY"), "actions/runs", Get("GITHUB_RUN_ID")),
                    Get("GITHUB_ACTOR"), null),
                Source(Get("GITHUB_REPOSITORY"), Get("GITHUB_SHA"), Get("GITHUB_REF_NAME")));

        if (IsTrue("TF_BUILD"))
        {
            var buildId = Get("BUILD_BUILDID");
            var project = Compose(Get("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI"), Get("SYSTEM_TEAMPROJECT"));
            return (new CiInfo("AzureDevOps", Get("BUILD_DEFINITIONNAME"), buildId, Get("SYSTEM_JOBATTEMPT"),
                    project == null || buildId == null ? null : $"{project}/_build/results?buildId={buildId}",
                    Get("BUILD_REQUESTEDFOR"), Get("ENVIRONMENT_NAME") ?? Get("RELEASE_ENVIRONMENTNAME")),
                Source(Get("BUILD_REPOSITORY_NAME"), Get("BUILD_SOURCEVERSION"), Get("BUILD_SOURCEBRANCHNAME")));
        }

        if (IsTrue("GITLAB_CI"))
            return (new CiInfo("GitLab", Get("CI_PROJECT_PATH"), Get("CI_PIPELINE_ID"), null, Get("CI_PIPELINE_URL"),
                    Get("GITLAB_USER_LOGIN"), Get("CI_ENVIRONMENT_NAME")),
                Source(Get("CI_PROJECT_PATH"), Get("CI_COMMIT_SHA"), Get("CI_COMMIT_REF_NAME")));

        if (Get("JENKINS_URL") != null || Get("JENKINS_HOME") != null)
            return (new CiInfo("Jenkins", Get("JOB_NAME"), Get("BUILD_NUMBER") ?? Get("BUILD_ID"), null, Get("BUILD_URL"), null, null),
                Source(null, Get("GIT_COMMIT"), JenkinsBranch(Get("GIT_BRANCH"))));

        return (null, null);
    }

    private static CiSource Source(string repository, string commit, string branch) =>
        repository == null && commit == null && branch == null ? null : new CiSource(repository, commit, branch);

    // The Git plugin names the branch as the remote sees it.
    private static string JenkinsBranch(string branch) =>
        branch == null ? null
        : branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..]
        : branch.StartsWith("origin/", StringComparison.Ordinal) ? branch["origin/".Length..]
        : branch;

    private static string Compose(params string[] parts) =>
        Array.Exists(parts, p => p == null) ? null : string.Join('/', Array.ConvertAll(parts, p => p.Trim('/')));
}
