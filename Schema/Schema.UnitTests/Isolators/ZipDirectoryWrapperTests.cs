// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using NSubstitute;
using Schema.Isolators;

namespace Schema.UnitTests.Isolators;

[TestFixture]
public class ZipDirectoryWrapperTests
{
    private static IZipEntry MockEntry(string fullName)
    {
        var entry = Substitute.For<IZipEntry>();
        entry.FullName.Returns(fullName);
        return entry;
    }

    private static IDirectory CreateWrapper(params IZipEntry[] entries)
    {
        return ZipDirectoryWrapper.GetFromFactory(new List<IZipEntry>(entries));
    }

    // --- Exists ---

    [Test]
    public void Exists_ReturnsTrue_WhenDirectoryHasEntries()
    {
        var wrapper = CreateWrapper(
            MockEntry("Templates/Main/Template.json"),
            MockEntry("Templates/Main/Tables/dbo.Test.json")
        );
        Assert.That(wrapper.Exists("Templates/Main"), Is.True);
    }

    [Test]
    public void Exists_ReturnsFalse_WhenDirectoryEmpty()
    {
        var wrapper = CreateWrapper(MockEntry("Other/file.txt"));
        Assert.That(wrapper.Exists("Templates/Main"), Is.False);
    }

    [Test]
    public void Exists_ReturnsFalse_WhenPathIsNull()
    {
        var wrapper = CreateWrapper(MockEntry("file.txt"));
        Assert.That(wrapper.Exists(null), Is.False);
    }

    [Test]
    public void Exists_ReturnsFalse_WhenPathIsEmpty()
    {
        var wrapper = CreateWrapper(MockEntry("file.txt"));
        Assert.That(wrapper.Exists(""), Is.False);
    }

    [Test]
    public void Exists_IsCaseInsensitive()
    {
        var wrapper = CreateWrapper(MockEntry("Templates/Main/file.json"));
        Assert.That(wrapper.Exists("templates/main"), Is.True);
    }

    // --- GetFiles ---

    [Test]
    public void GetFiles_ReturnsMatchingFiles()
    {
        var wrapper = CreateWrapper(
            MockEntry("Tables/dbo.One.json"),
            MockEntry("Tables/dbo.Two.json"),
            MockEntry("Tables/SubDir/dbo.Three.json")
        );
        var files = wrapper.GetFiles("Tables", "*.json", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(3));
    }

    [Test]
    public void GetFiles_TopDirectoryOnly_ExcludesSubdirectories()
    {
        var wrapper = CreateWrapper(
            MockEntry("Tables/dbo.One.json"),
            MockEntry("Tables/SubDir/dbo.Two.json")
        );
        var files = wrapper.GetFiles("Tables", "*.json", SearchOption.TopDirectoryOnly);
        Assert.That(files, Has.Length.EqualTo(1));
        Assert.That(files[0], Does.Contain("dbo.One.json"));
    }

    [Test]
    public void GetFiles_ReturnsEmpty_WhenPathIsNull()
    {
        var wrapper = CreateWrapper(MockEntry("file.txt"));
        Assert.That(wrapper.GetFiles(null, "*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void GetFiles_ReturnsEmpty_WhenNoMatches()
    {
        var wrapper = CreateWrapper(MockEntry("Tables/file.txt"));
        var files = wrapper.GetFiles("Tables", "*.json", SearchOption.AllDirectories);
        Assert.That(files, Is.Empty);
    }

    [Test]
    public void GetFiles_MatchesWildcardPattern()
    {
        var wrapper = CreateWrapper(
            MockEntry("Scripts/setup.sql"),
            MockEntry("Scripts/cleanup.sql"),
            MockEntry("Scripts/readme.txt")
        );
        var files = wrapper.GetFiles("Scripts", "*.sql", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(2));
    }

    [Test]
    public void GetFiles_ExcludesDirectoryEntries()
    {
        var wrapper = CreateWrapper(
            MockEntry("Tables/"),
            MockEntry("Tables/dbo.Test.json")
        );
        var files = wrapper.GetFiles("Tables", "*", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(1));
        Assert.That(files[0], Does.Not.EndWith("/"));
    }

    // --- GetDirectories ---

    [Test]
    public void GetDirectories_ListsTheImmediateSubfolders_Once()
    {
        var wrapper = CreateWrapper(
            MockEntry("Templates/Main/Template.json"),
            MockEntry("Templates/Main/Tables/dbo.Test.json"),
            MockEntry("Templates/Other/Template.json"),
            MockEntry("Product.json"));

        Assert.That(wrapper.GetDirectories("Templates", "*", SearchOption.TopDirectoryOnly),
            Is.EquivalentTo(new[] { "Templates/Main", "Templates/Other" }));
    }
}
