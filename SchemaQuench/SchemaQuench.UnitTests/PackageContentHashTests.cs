// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Schema.Isolators;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class PackageContentHashTests
{
    private readonly List<string> _dirs = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in _dirs.Where(Directory.Exists)) Directory.Delete(dir, true);
        _dirs.Clear();
    }

    private string Package(params (string Path, string Content)[] files)
    {
        var root = Path.Join(Path.GetTempPath(), "pkghash_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(root);
        foreach (var (path, content) in files)
        {
            var full = Path.Join(root, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
        }
        return root;
    }

    private static readonly (string, string)[] Standard =
    [
        ("Product.json", "{\r\n  \"Name\": \"P\"\r\n}\r\n"),
        ("Templates/Main/Template.json", "{\r\n  \"Name\": \"Main\"\r\n}\r\n"),
        ("Templates/Main/Procedures/dbo.Get.sql", "CREATE PROCEDURE dbo.Get AS\r\nSELECT 1;\r\n")
    ];

    private static (string, string)[] WithLf((string Path, string Content)[] files) =>
        files.Select(f => (f.Path, f.Content.Replace("\r\n", "\n"))).ToArray();

    [Test]
    public void CrlfAndLfCopies_HashTheSame()
    {
        var crlf = PackageContentHash.ForFolder(Package(Standard));
        var lf = PackageContentHash.ForFolder(Package(WithLf(Standard)));

        Assert.That(crlf, Is.Not.Null);
        Assert.That(lf.ContentHash, Is.EqualTo(crlf.ContentHash));
    }

    [Test]
    public void AFolderAndItsZip_HashTheSame_WhateverTheEntryOrderSeparatorOrDirectoryEntries()
    {
        var folder = PackageContentHash.ForFolder(Package(Standard));

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("Templates\\");
            foreach (var (path, content) in Enumerable.Reverse(Standard))
            {
                using var entry = zip.CreateEntry(path.Replace('/', '\\')).Open();
                entry.Write(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
            }
        }
        stream.Position = 0;
        using var read = new ZipArchive(stream, ZipArchiveMode.Read);
        var zipped = PackageContentHash.ForZip(read.Entries.Select(e => (IZipEntry)new ZipEntryWrapper(e)));

        Assert.That(zipped.ContentHash, Is.EqualTo(folder.ContentHash));
        Assert.That(zipped.FileCount, Is.EqualTo(3));
    }

    [Test]
    public void OneByteChanged_ChangesTheHash()
    {
        var changed = Standard.ToArray();
        changed[2] = (changed[2].Item1, changed[2].Item2.Replace("SELECT 1", "SELECT 2"));

        Assert.That(PackageContentHash.ForFolder(Package(changed)).ContentHash,
            Is.Not.EqualTo(PackageContentHash.ForFolder(Package(Standard)).ContentHash));
    }

    [Test]
    public void AFileAdded_ChangesTheHash()
    {
        var added = Standard.Append(("Templates/Main/Views/dbo.V.sql", "CREATE VIEW dbo.V AS SELECT 1 AS A;")).ToArray();

        Assert.That(PackageContentHash.ForFolder(Package(added)).ContentHash,
            Is.Not.EqualTo(PackageContentHash.ForFolder(Package(Standard)).ContentHash));
    }

    [Test]
    public void AFileRenamed_ChangesTheHash()
    {
        var renamed = Standard.ToArray();
        renamed[2] = ("Templates/Main/Procedures/dbo.Get2.sql", renamed[2].Item2);

        Assert.That(PackageContentHash.ForFolder(Package(renamed)).ContentHash,
            Is.Not.EqualTo(PackageContentHash.ForFolder(Package(Standard)).ContentHash));
    }

    [Test]
    public void SqlErrorFiles_DotDirectories_AndAnInPackageLogDirectory_AreNotHashed()
    {
        var plain = PackageContentHash.ForFolder(Package(Standard));
        var cluttered = Package(Standard.Concat(new[]
        {
            ("Templates/Main/Procedures/dbo.Bad.sqlerror", "CREATE PROC broken"),
            (".json-schemas/products.schema", "{}"),
            (".git/HEAD", "ref: refs/heads/main\n"),
            ("Templates/Main/.hidden/x.sql", "SELECT 1"),
            ("Logs/SchemaQuench - Progress.log", "a log line")
        }).ToArray());

        var result = PackageContentHash.ForFolder(cluttered, Path.Join(cluttered, "Logs"));

        Assert.That(result.ContentHash, Is.EqualTo(plain.ContentHash));
        Assert.That(result.FileCount, Is.EqualTo(3));
    }

    [Test]
    public void Format_IsSha256Prefix_With64LowerHexChars()
    {
        var result = PackageContentHash.ForFolder(Package(Standard));

        Assert.That(result.ContentHash, Does.Match("^sha256:[0-9a-f]{64}$"));
        Assert.That(result.FileCount, Is.EqualTo(3));
    }

    // Large data files are hashed in chunks; a CRLF split across two reads is still one line end.
    [TestCase(81919)]
    [TestCase(81920)]
    [TestCase(163839)]
    public void ACrlfAtAReadBoundary_HashesAsLf(int prefix)
    {
        var body = new string('a', prefix);
        var crlf = PackageContentHash.ForFolder(Package(("Product.json", body + "\r\nend\r\n")));
        var lf = PackageContentHash.ForFolder(Package(("Product.json", body + "\nend\n")));

        Assert.That(lf.ContentHash, Is.EqualTo(crlf.ContentHash));
    }

    [Test]
    public void ALoneCarriageReturnAtAReadBoundary_IsContent()
    {
        var body = new string('a', 81919);
        Assert.That(PackageContentHash.ForFolder(Package(("Product.json", body + "\rb"))).ContentHash,
            Is.Not.EqualTo(PackageContentHash.ForFolder(Package(("Product.json", body + "b"))).ContentHash));
    }

    [TestCase("a\rb", "ab")]
    [TestCase("a\r\r\nb", "a\nb")]
    [TestCase("ab\r", "ab")]
    public void ACarriageReturnNotBeforeALineFeed_IsContent(string withCr, string without)
    {
        Assert.That(PackageContentHash.ForFolder(Package(("Product.json", withCr))).ContentHash,
            Is.Not.EqualTo(PackageContentHash.ForFolder(Package(("Product.json", without))).ContentHash));
    }

    // A run writes into the package folder when --LogPath or ArtifactPath points there: its logs, its summary, its debug SQL
    // and its log backups change while the hash is read, and are not the package.
    [Test]
    public void TheToolsOwnOutput_IsNotHashed_WhereverItIs()
    {
        var plain = PackageContentHash.ForFolder(Package(Standard));
        var withOutput = PackageContentHash.ForFolder(Package(Standard.Concat(new[]
        {
            ("SchemaQuench - Progress.log", "a line"),
            ("SchemaQuench - Summary.json", "{}"),
            ("SchemaQuench - Main TestMain dbo.Get.sql", "-- SchemaSmith debug SQL"),
            ("SchemaTongs - Errors.log", ""),
            ("SchemaQuench.0003/SchemaQuench - Progress.log", "an older run"),
            ("Templates/Main/DataTongs - Progress.log", "")
        }).ToArray()));

        Assert.That(withOutput.ContentHash, Is.EqualTo(plain.ContentHash));
        Assert.That(withOutput.FileCount, Is.EqualTo(3));
    }

    [Test]
    public void AMissingFolder_GivesNull()
    {
        Assert.That(PackageContentHash.ForFolder(Path.Join(Path.GetTempPath(), "no_such_" + Guid.NewGuid().ToString("N"))), Is.Null);
    }
}
