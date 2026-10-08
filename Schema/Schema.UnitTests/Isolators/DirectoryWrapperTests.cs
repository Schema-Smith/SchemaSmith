// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using Schema.Isolators;

namespace Schema.UnitTests.Isolators;

[TestFixture]
public class DirectoryWrapperTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), "schemasmith-dirwrapper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Join(_root, "sub"));
        File.WriteAllText(Path.Join(_root, "top.sql"), "x");
        File.WriteAllText(Path.Join(_root, "top.txt"), "x");
        File.WriteAllText(Path.Join(_root, "sub", "nested.sql"), "x");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void EnumerateFiles_TopDirectoryOnly_ExcludesSubdirectories()
    {
        var files = DirectoryWrapper.GetFromFactory()
            .EnumerateFiles(_root, "*.sql", SearchOption.TopDirectoryOnly)
            .ToList();

        Assert.That(files, Has.Count.EqualTo(1));
        Assert.That(files[0], Does.EndWith("top.sql"));
    }

    [Test]
    public void EnumerateFiles_AllDirectories_IncludesNested()
    {
        var files = DirectoryWrapper.GetFromFactory()
            .EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToList();

        Assert.That(files, Is.EquivalentTo(new[] { "top.sql", "nested.sql" }));
    }

    [Test]
    public void EnumerateFileSystemEntries_ReturnsBothFilesAndDirectories()
    {
        var entries = DirectoryWrapper.GetFromFactory()
            .EnumerateFileSystemEntries(_root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToList();

        Assert.That(entries, Is.EquivalentTo(new[] { "top.sql", "top.txt", "sub" }));
    }

    // ---- EnumerateFilesWithTimestamps ----
    //
    // Exists so a caller wanting "the N most recently written files" does not pay one stat per candidate.
    // The platform hands the walk each entry's write time already; IEnumerable<string> threw it away, so
    // ordering a 50,000-file directory by write time cost ~3,500ms of stat calls instead of ~100ms.

    [Test]
    public void EnumerateFilesWithTimestamps_ReturnsPathAndLastWriteTime()
    {
        var expected = File.GetLastWriteTimeUtc(Path.Join(_root, "top.sql"));

        var files = DirectoryWrapper.GetFromFactory()
            .EnumerateFilesWithTimestamps(_root, "*.sql", SearchOption.TopDirectoryOnly)
            .ToList();

        Assert.That(files, Has.Count.EqualTo(1));
        Assert.That(files[0].Path, Does.EndWith("top.sql"));
        Assert.That(files[0].LastWriteTimeUtc, Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)),
            "the whole point is that the timestamp comes back with the entry, so it has to be the "
            + "entry's real write time and not something approximated");
    }

    [Test]
    public void EnumerateFilesWithTimestamps_HonoursSearchOptionAndPattern()
    {
        var wrapper = DirectoryWrapper.GetFromFactory();

        Assert.That(wrapper.EnumerateFilesWithTimestamps(_root, "*.sql", SearchOption.AllDirectories).ToList(),
            Has.Count.EqualTo(2), "AllDirectories must reach the nested file");
        Assert.That(wrapper.EnumerateFilesWithTimestamps(_root, "*.txt", SearchOption.TopDirectoryOnly).ToList(),
            Has.Count.EqualTo(1), "the pattern must filter the same way the string overload does");
    }

    [Test]
    public void EnumerateFilesWithTimestamps_IsLazy()
    {
        // Deliberately not a timing test. The enumerable is built BEFORE the new file exists: a lazy walk
        // finds it when enumerated, an eager one snapshotted the directory at call time and cannot. That
        // makes this deterministic, and it fails for the reason it is about.
        //
        // Laziness is the requirement, not an optimisation: the only bound a caller has on a directory of
        // unknown size is its ability to stop walking. A version that materialises first puts back exactly
        // the stall this method exists to remove.
        var lazyEnumerable = DirectoryWrapper.GetFromFactory()
            .EnumerateFilesWithTimestamps(_root, "*.sql", SearchOption.TopDirectoryOnly);

        File.WriteAllText(Path.Join(_root, "appeared-after.sql"), "x");

        Assert.That(lazyEnumerable.Select(f => Path.GetFileName(f.Path)), Does.Contain("appeared-after.sql"),
            "the enumeration must be deferred -- a file created after the call but before enumeration has "
            + "to appear, or the walk was materialised up front");
    }

    [Test]
    public void EnumerateFilesWithTimestamps_OrdersByWriteTimeWithoutExtraStats()
    {
        // The motivating use: newest-first. Asserted as an outcome -- the newest file comes back first --
        // rather than by counting syscalls, which would pin the mechanism instead of the result.
        var newer = Path.Join(_root, "newer.sql");
        File.WriteAllText(newer, "x");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(1));

        var newestFirst = DirectoryWrapper.GetFromFactory()
            .EnumerateFilesWithTimestamps(_root, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        Assert.That(newestFirst.First(), Is.EqualTo("newer.sql"));
    }

    // Windows always matched a pattern without regard to case; Linux and macOS did not, so "status.SQL" was a script on
    // one OS and invisible on the others.
    [Test]
    public void GetFiles_MatchesThePatternWhateverTheFileNameCase()
    {
        File.WriteAllText(Path.Join(_root, "Upper.SQL"), "x");

        var files = new DirectoryWrapper().GetFiles(_root, "*.sql", SearchOption.TopDirectoryOnly).Select(Path.GetFileName);

        Assert.That(files, Does.Contain("Upper.SQL"));
    }
}
