// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using Schema.Isolators;

namespace SchemaQuench.Reporting;

public sealed record GitSource(string Commit, string Branch);

/// <summary>
/// The commit and branch of the repository a package sits in, read from the files under <c>.git</c> so no <c>git</c>
/// executable is needed. A worktree or submodule's <c>.git</c> file is followed through its <c>gitdir:</c> line, and its
/// refs through <c>commondir</c>. Anything that does not resolve is null rather than an error.
/// </summary>
public static class GitSourceReader
{
    private const string RefPrefix = "ref:";
    private const string HeadsPrefix = "refs/heads/";

    public static GitSource Read(string startDirectory, string stopAt = null)
    {
        try
        {
            var gitDir = FindGitDirectory(startDirectory, stopAt);
            if (gitDir == null) return null;

            var file = FileWrapper.GetFromFactory();
            var headPath = Path.Join(gitDir, "HEAD");
            if (!file.Exists(headPath)) return null;
            var head = file.ReadAllText(headPath).Trim();

            if (!head.StartsWith(RefPrefix, StringComparison.Ordinal))
                return new GitSource(IsCommitId(head) ? head : null, null);

            var reference = head[RefPrefix.Length..].Trim();
            var branch = reference.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? reference[HeadsPrefix.Length..] : reference;
            return new GitSource(ResolveRef(gitDir, reference), branch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string FindGitDirectory(string startDirectory, string stopAt)
    {
        var directory = DirectoryWrapper.GetFromFactory();
        var file = FileWrapper.GetFromFactory();
        var stop = string.IsNullOrEmpty(stopAt) ? null : Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = Path.GetFullPath(startDirectory); current != null; current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Join(current, ".git");
            if (directory.Exists(candidate)) return candidate;
            if (file.Exists(candidate)) return FollowGitFile(candidate, file, directory);
            if (stop != null && string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return null;
    }

    private static string FollowGitFile(string gitFile, IFile file, IDirectory directory)
    {
        var line = file.ReadAllLines(gitFile).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line == null) return null;
        var target = line["gitdir:".Length..].Trim();
        var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Join(Path.GetDirectoryName(gitFile), target));
        return directory.Exists(resolved) ? resolved : null;
    }

    private static string ResolveRef(string gitDir, string reference)
    {
        var file = FileWrapper.GetFromFactory();
        foreach (var dir in new[] { gitDir, CommonDirectory(gitDir, file) }.Where(d => d != null).Distinct())
        {
            var loose = Path.Join(dir, reference);
            if (file.Exists(loose))
            {
                var value = file.ReadAllText(loose).Trim();
                if (IsCommitId(value)) return value;
            }

            var packed = Path.Join(dir, "packed-refs");
            if (!file.Exists(packed)) continue;
            foreach (var line in file.ReadAllLines(packed))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length == 2 && parts[1].Trim() == reference && IsCommitId(parts[0])) return parts[0];
            }
        }
        return null;
    }

    private static string CommonDirectory(string gitDir, IFile file)
    {
        var commondir = Path.Join(gitDir, "commondir");
        if (!file.Exists(commondir)) return null;
        var target = file.ReadAllText(commondir).Trim();
        return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Join(gitDir, target));
    }

    // SHA-1 (40) or SHA-256 (64) object names.
    private static bool IsCommitId(string value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
