// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using NUnit.Framework;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class GitSourceReaderTests
{
    private const string Sha = "4f2a9c1e0b7d3a8f6e5c4b3a29180716f5e4d3c2";
    private const string OtherSha = "aaaabbbbccccddddeeeeffff0000111122223333";
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), "gitsrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Join(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string PackageIn(string relative)
    {
        var dir = Path.Join(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void ABranchRef_GivesCommitAndBranch_FromAPackageBelowTheRepositoryRoot()
    {
        Write("repo/.git/HEAD", "ref: refs/heads/release/2.9\n");
        Write("repo/.git/refs/heads/release/2.9", Sha + "\n");

        var source = GitSourceReader.Read(PackageIn("repo/Demos/Package"));

        Assert.That(source, Is.EqualTo(new GitSource(Sha, "release/2.9")));
    }

    [Test]
    public void APackedRef_GivesCommit_SkippingCommentsAndPeeledLines()
    {
        Write("repo/.git/HEAD", "ref: refs/heads/main\n");
        Write("repo/.git/packed-refs",
            "# pack-refs with: peeled fully-peeled sorted\n" +
            $"{OtherSha} refs/tags/v1\n^{OtherSha}\n" +
            $"{Sha} refs/heads/main\n");

        var source = GitSourceReader.Read(PackageIn("repo"));

        Assert.That(source, Is.EqualTo(new GitSource(Sha, "main")));
    }

    [Test]
    public void ALooseRef_WinsOverAPackedOne()
    {
        Write("repo/.git/HEAD", "ref: refs/heads/main\n");
        Write("repo/.git/packed-refs", $"{OtherSha} refs/heads/main\n");
        Write("repo/.git/refs/heads/main", Sha + "\n");

        Assert.That(GitSourceReader.Read(PackageIn("repo"))!.Commit, Is.EqualTo(Sha));
    }

    [Test]
    public void ADetachedHead_GivesCommit_AndNullBranch()
    {
        Write("repo/.git/HEAD", Sha + "\n");

        Assert.That(GitSourceReader.Read(PackageIn("repo")), Is.EqualTo(new GitSource(Sha, null)));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AWorktreeGitFile_IsFollowed_WithRefsInTheCommonDirectory(bool relative)
    {
        Write("main/.git/HEAD", "ref: refs/heads/main\n");
        Write("main/.git/refs/heads/feature", Sha + "\n");
        Write("main/.git/worktrees/wt/HEAD", "ref: refs/heads/feature\n");
        Write("main/.git/worktrees/wt/commondir", "../..\n");
        var gitdir = relative ? "../main/.git/worktrees/wt" : Path.Join(_root, "main", ".git", "worktrees", "wt");
        Write("wt/.git", $"gitdir: {gitdir}\n");

        Assert.That(GitSourceReader.Read(PackageIn("wt/Package")), Is.EqualTo(new GitSource(Sha, "feature")));
    }

    [Test]
    public void AnUnresolvableRef_GivesBranch_AndNullCommit()
    {
        Write("repo/.git/HEAD", "ref: refs/heads/gone\n");

        Assert.That(GitSourceReader.Read(PackageIn("repo")), Is.EqualTo(new GitSource(null, "gone")));
    }

    [Test]
    public void AGitFileThatPointsNowhere_GivesNull()
    {
        Write("repo/.git", "gitdir: ../nowhere\n");

        Assert.That(GitSourceReader.Read(PackageIn("repo")), Is.Null);
    }

    [Test]
    public void NoRepository_GivesNull()
    {
        Assert.That(GitSourceReader.Read(PackageIn("plain/Package"), stopAt: _root), Is.Null);
    }
}
