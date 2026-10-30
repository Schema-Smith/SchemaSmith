// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Schema.UnitTests;

/// <summary>
/// Whether CI runs for a change is decided by one list, .github/ci-tested-paths.txt. CI's own trigger cannot read a file,
/// so it carries a copy, and these tests hold the copies to the list. They also hold every tracked top-level path to a
/// decision: a new directory lands on neither side by default, and that is the drift that had already happened once.
/// </summary>
[TestFixture]
public class CiTestedPathsTests
{
    private static string RepoFile(params string[] parts) => Path.Join([RepoSources.Root, .. parts]);

    private static (List<string> Tested, List<string> Untested) ReadList()
    {
        var tested = new List<string>();
        var untested = new List<string>();
        List<string> section = null;
        foreach (var raw in File.ReadAllLines(RepoFile(".github", "ci-tested-paths.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "[tested]") { section = tested; continue; }
            if (line == "[untested]") { section = untested; continue; }
            section?.Add(line);
        }
        return (tested, untested);
    }

    // The `paths:` list that follows each trigger in continuous-integration.yml, in order.
    private static List<List<string>> TriggerCopies()
    {
        var lines = File.ReadAllLines(RepoFile(".github", "workflows", "continuous-integration.yml"));
        var copies = new List<List<string>>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != "    paths:") continue;
            var copy = new List<string>();
            for (var j = i + 1; j < lines.Length && lines[j].StartsWith("      - '"); j++)
                copy.Add(lines[j].Trim()[3..^1]);
            copies.Add(copy);
        }
        return copies;
    }

    [Test]
    public void BothCiTriggers_CarryTheTestedList_AndNoIgnoreList()
    {
        var (tested, _) = ReadList();
        var copies = TriggerCopies();
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "continuous-integration.yml"));
        Assert.Multiple(() =>
        {
            Assert.That(copies, Has.Count.EqualTo(2), "a paths list on the push trigger and on the pull_request trigger");
            foreach (var copy in copies)
                Assert.That(copy, Is.EqualTo(tested), "the trigger's paths, in order, are .github/ci-tested-paths.txt's [tested] lines");
            Assert.That(workflow, Does.Not.Contain("paths-ignore"), "an ignore list is wrong by default for every new file");
        });
    }

    [Test]
    public void TheCompanionAndTheReleaseGate_ReadTheList()
    {
        var companion = File.ReadAllText(RepoFile(".github", "workflows", "ci-docs-only-companion.yml"));
        var release = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));
        const string call = "python3 scripts/ci_path_filter.py .github/ci-tested-paths.txt";
        Assert.Multiple(() =>
        {
            Assert.That(companion, Does.Contain(call), "the companion's gate asks the list");
            Assert.That(companion, Does.Not.Contain("    paths:").And.Not.Contain("paths-ignore"),
                "and starts on every pull request, so its trigger cannot disagree with CI's");
            Assert.That(release, Does.Contain(call), "the release walk-back asks the list");
        });
    }

    [Test]
    public void EveryTrackedTopLevelPath_IsOnOneSide()
    {
        var (tested, untested) = ReadList();
        var testedTops = tested.Where(p => !p.StartsWith('!') && !p.StartsWith('*'))
            .Select(p => p.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        var tops = GitLsFiles().Select(f => f.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        Assert.That(tops, Has.Count.GreaterThan(10), "git ls-files listed the repository");
        Assert.Multiple(() =>
        {
            Assert.That(tops.Where(t => !testedTops.Contains(t) && !untested.Contains(t)), Is.Empty,
                "a tracked top-level path on neither list in .github/ci-tested-paths.txt: decide whether CI reads it");
            Assert.That(untested.Where(u => testedTops.Contains(u) && !IsOnlyPartlyTested(u, tested)), Is.Empty,
                "a path listed as untested whose files are all tested");
        });
    }

    // docs, scripts and .github are untested apart from the files CI reads, which [tested] names one by one.
    private static bool IsOnlyPartlyTested(string top, List<string> tested) =>
        !tested.Contains(top + "/**");

    [Test]
    public void TheMatcher_PassesItsSelfTest()
    {
        var (exit, output) = Python(RepoFile("scripts", "ci_path_filter.py"), "--selftest");
        Assert.That(exit, Is.EqualTo(0), output);
    }

    private static List<string> GitLsFiles()
    {
        var psi = new ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = RepoSources.Root, RedirectStandardOutput = true, UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var files = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToList();
        process.WaitForExit();
        return files;
    }

    internal static (int Exit, string Output) Python(params string[] args)
    {
        foreach (var exe in new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var process = Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, output);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on this machine under that name; try the next.
            }
        }
        Assert.Fail("no python3 or python on PATH; the CI path matcher cannot be checked");
        return (-1, "");
    }
}
