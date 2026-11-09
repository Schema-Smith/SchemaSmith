// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Configuration;
using Schema.Isolators;

namespace SchemaQuench.Reporting;

/// <summary>Where the package came from. <see cref="From"/> names what supplied the commit: setting, ci or git.</summary>
public sealed record RunSource(string Repository, string Commit, string Branch, string From);

/// <summary>Who ran the process: names only, never a credential.</summary>
public sealed record RunHost(string Machine, string User)
{
    public static RunHost Current() => From(System.Environment.MachineName, System.Environment.UserDomainName, System.Environment.UserName);

    // A local account's "domain" is the machine itself, which adds nothing.
    public static RunHost From(string machine, string domain, string user) =>
        new(machine, string.IsNullOrEmpty(domain) || string.Equals(domain, machine, StringComparison.OrdinalIgnoreCase) ? user : $"{domain}\\{user}");
}

/// <summary>
/// Which run a deployment summary describes. Each source field is taken from the first of a Run* setting, the CI
/// system's environment and the package's own repository that has it, field by field.
/// </summary>
public sealed record RunIdentity(Guid Id, string Label, Guid? ResumesRunId, PackageIdentity Package, RunSource Source, CiInfo Ci, RunHost Host)
{
    public static RunIdentity Build(Guid id, Func<string, string> setting, IEnvironment environment, GitSource git,
        PackageIdentity package, RunHost host)
    {
        string Set(string key) => string.IsNullOrWhiteSpace(setting(key)) ? null : setting(key).Trim();
        var (ci, ciSource) = CiEnvironment.Detect(environment);
        return new RunIdentity(id, Set(SettingsKeys.Run.Label), null, package, BuildSource(Set, ciSource, git), BuildCi(Set, ci), host);
    }

    public RunIdentity WithResume(Guid? resumesRunId) => this with { ResumesRunId = resumesRunId };

    private static RunSource BuildSource(Func<string, string> set, CiSource ci, GitSource git)
    {
        var (repository, repositoryFrom) = First((set(SettingsKeys.Run.Repository), "setting"), (ci?.Repository, "ci"));
        var (commit, commitFrom) = First((set(SettingsKeys.Run.Commit), "setting"), (ci?.Commit, "ci"), (git?.Commit, "git"));
        var (branch, branchFrom) = First((set(SettingsKeys.Run.Branch), "setting"), (ci?.Branch, "ci"), (git?.Branch, "git"));
        if (repository == null && commit == null && branch == null) return null;
        return new RunSource(repository, commit, branch, commitFrom ?? branchFrom ?? repositoryFrom);
    }

    private static CiInfo BuildCi(Func<string, string> set, CiInfo detected)
    {
        var pipeline = set(SettingsKeys.Run.Pipeline);
        var runId = set(SettingsKeys.Run.PipelineRunId);
        var url = set(SettingsKeys.Run.Url);
        var actor = set(SettingsKeys.Run.Actor);
        var environment = set(SettingsKeys.Run.Environment);
        if (detected == null && pipeline == null && runId == null && url == null && actor == null && environment == null) return null;
        return new CiInfo(detected?.Provider ?? "Other", pipeline ?? detected?.Pipeline, runId ?? detected?.RunId, detected?.RunAttempt,
            url ?? detected?.Url, actor ?? detected?.Actor, environment ?? detected?.Environment);
    }

    private static (string Value, string From) First(params (string Value, string From)[] candidates) =>
        Array.Find(candidates, c => c.Value != null) is var hit && hit.Value != null ? hit : (null, null);
}
