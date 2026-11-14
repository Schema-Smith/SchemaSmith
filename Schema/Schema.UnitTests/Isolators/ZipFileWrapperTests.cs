// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Text;
using System.IO.Compression;
using NSubstitute;
using Schema.Isolators;

namespace Schema.UnitTests.Isolators;

[TestFixture]
public class ZipFileWrapperTests
{
    private ZipFileWrapper _wrapper;

    [SetUp]
    public void SetUp()
    {
        _wrapper = new ZipFileWrapper();
    }

    [TearDown]
    public void TearDown()
    {
        _wrapper.Dispose();
        FactoryContainer.Clear();
    }

    private static IZipEntry MockEntry(string fullName, string content = "")
    {
        var entry = Substitute.For<IZipEntry>();
        entry.FullName.Returns(fullName);
        entry.Open().Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(content)));
        return entry;
    }

    private static IZipEntry MockBinaryEntry(string fullName, byte[] content)
    {
        var entry = Substitute.For<IZipEntry>();
        entry.FullName.Returns(fullName);
        entry.Open().Returns(_ => new MemoryStream(content));
        return entry;
    }

    // A package load asks the factory for the zip again for each file it reads; every one of those must not leave the
    // archive open behind it, or the zip stays locked after the run is done with it.
    [Test]
    public void GettingTheZipAgain_LeavesOnlyOneArchiveOpen()
    {
        var dir = Path.Join(Path.GetTempPath(), "ssmith_zip_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(dir, "src"));
        File.WriteAllText(Path.Join(dir, "src", "Product.json"), "{}");
        var zip = Path.Join(dir, "pkg.zip");
        ZipFile.CreateFromDirectory(Path.Join(dir, "src"), zip);
        try
        {
            ZipFileWrapper.GetFromFactory(zip);
            ZipFileWrapper.GetFromFactory(zip);
            var wrapper = (ZipFileWrapper)ZipFileWrapper.GetFromFactory(zip);
            Assert.That(wrapper.Exists("Product.json"), Is.True, "still reads after being asked again");

            wrapper.Dispose();

            Assert.That(() => File.Delete(zip), Throws.Nothing, "disposing the wrapper releases the zip");
        }
        finally
        {
            FactoryContainer.Clear();
            Directory.Delete(dir, true);
        }
    }

    // --- Exists ---

    [Test]
    public void Exists_ReturnsTrue_WhenEntryFound()
    {
        _wrapper.SetZipEntries([MockEntry("Templates/Main/Template.json")]);
        Assert.That(_wrapper.Exists("Templates/Main/Template.json"), Is.True);
    }

    [Test]
    public void Exists_ReturnsFalse_WhenEntryNotFound()
    {
        _wrapper.SetZipEntries([MockEntry("Templates/Main/Template.json")]);
        Assert.That(_wrapper.Exists("Templates/Other/Missing.json"), Is.False);
    }

    [Test]
    public void Exists_IsCaseInsensitive()
    {
        _wrapper.SetZipEntries([MockEntry("Templates/Main/Template.json")]);
        Assert.That(_wrapper.Exists("templates/main/template.json"), Is.True);
    }

    [Test]
    public void Exists_NormalizesSlashes()
    {
        _wrapper.SetZipEntries([MockEntry("Templates/Main/Template.json")]);
        Assert.That(_wrapper.Exists(@"Templates\Main\Template.json"), Is.True);
    }

    [Test]
    public void Exists_ReturnsFalse_WhenPathIsNullOrEmpty()
    {
        _wrapper.SetZipEntries([MockEntry("some/file.txt")]);
        Assert.Multiple(() =>
        {
            Assert.That(_wrapper.Exists(null), Is.False);
            Assert.That(_wrapper.Exists(""), Is.False);
        });
    }

    // --- ReadAllText ---

    [Test]
    public void ReadAllText_ReturnsContent_WhenEntryFound()
    {
        _wrapper.SetZipEntries([MockEntry("Product.json", """{"Name":"Test"}""")]);
        var content = _wrapper.ReadAllText("Product.json");
        Assert.That(content, Is.EqualTo("""{"Name":"Test"}"""));
    }

    [Test]
    public void ReadAllText_IsCaseInsensitive()
    {
        _wrapper.SetZipEntries([MockEntry("Product.json", "content")]);
        Assert.That(_wrapper.ReadAllText("product.json"), Is.EqualTo("content"));
    }

    [Test]
    public void ReadAllText_NormalizesSlashes()
    {
        _wrapper.SetZipEntries([MockEntry("Templates/Main/Table.json", "data")]);
        Assert.That(_wrapper.ReadAllText(@"Templates\Main\Table.json"), Is.EqualTo("data"));
    }

    [Test]
    public void ReadAllText_ThrowsFileNotFound_WhenEntryMissing()
    {
        _wrapper.SetZipEntries([MockEntry("other.json")]);
        var ex = Assert.Throws<FileNotFoundException>(() => _wrapper.ReadAllText("missing.json"));
        Assert.That(ex!.Message, Does.Contain("missing.json"));
    }

    [Test]
    public void ReadAllText_ThrowsFileNotFound_WhenPathIsNullOrEmpty()
    {
        _wrapper.SetZipEntries([MockEntry("file.txt")]);
        Assert.Multiple(() =>
        {
            Assert.Throws<FileNotFoundException>(() => _wrapper.ReadAllText(null));
            Assert.Throws<FileNotFoundException>(() => _wrapper.ReadAllText(""));
        });
    }

    // --- ReadAllBytes ---

    [Test]
    public void ReadAllBytes_ReturnsContent_WhenEntryFound()
    {
        var bytes = new byte[] { 0, 1, 2, 255 };
        _wrapper.SetZipEntries([MockBinaryEntry("Templates/Main/blob.bin", bytes)]);

        var content = _wrapper.ReadAllBytes("Templates/Main/blob.bin");

        Assert.That(content, Is.EqualTo(bytes));
    }

    [Test]
    public void ReadAllBytes_IsCaseInsensitive()
    {
        var bytes = new byte[] { 10, 20, 30 };
        _wrapper.SetZipEntries([MockBinaryEntry("ProductData/File.bin", bytes)]);

        Assert.That(_wrapper.ReadAllBytes("productdata/file.bin"), Is.EqualTo(bytes));
    }

    [Test]
    public void ReadAllBytes_NormalizesSlashes()
    {
        var bytes = new byte[] { 42 };
        _wrapper.SetZipEntries([MockBinaryEntry("Templates/Main/File.bin", bytes)]);

        Assert.That(_wrapper.ReadAllBytes(@"Templates\Main\File.bin"), Is.EqualTo(bytes));
    }

    [Test]
    public void ReadAllBytes_ThrowsFileNotFound_WhenEntryMissing()
    {
        _wrapper.SetZipEntries([MockBinaryEntry("other.bin", [1])]);

        var ex = Assert.Throws<FileNotFoundException>(() => _wrapper.ReadAllBytes("missing.bin"));

        Assert.That(ex!.Message, Does.Contain("missing.bin"));
    }

    [Test]
    public void ReadAllBytes_ThrowsFileNotFound_WhenPathIsNullOrEmpty()
    {
        _wrapper.SetZipEntries([MockBinaryEntry("file.bin", [1])]);
        Assert.Multiple(() =>
        {
            Assert.Throws<FileNotFoundException>(() => _wrapper.ReadAllBytes(null));
            Assert.Throws<FileNotFoundException>(() => _wrapper.ReadAllBytes(""));
        });
    }

    // --- IsValidZipFile ---

    [Test]
    public void IsValidZipFile_ReturnsFalse_WhenFilenameIsNull()
    {
        Assert.That(ZipFileWrapper.IsValidZipFile(null), Is.False);
    }

    [Test]
    public void IsValidZipFile_ReturnsFalse_WhenFilenameIsEmpty()
    {
        Assert.That(ZipFileWrapper.IsValidZipFile(""), Is.False);
    }

    [Test]
    public void IsValidZipFile_ReturnsFalse_WhenFileDoesNotExist()
    {
        var mockFile = Substitute.For<IFile>();
        mockFile.Exists("missing.zip").Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFile);
            Assert.That(ZipFileWrapper.IsValidZipFile("missing.zip"), Is.False);
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void IsValidZipFile_ReturnsFalse_WhenFileIsEmpty()
    {
        var mockFile = Substitute.For<IFile>();
        mockFile.Exists("empty.zip").Returns(true);
        mockFile.OpenRead("empty.zip").Returns(new MemoryStream([]));
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFile);
            Assert.That(ZipFileWrapper.IsValidZipFile("empty.zip"), Is.False);
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void IsValidZipFile_ReturnsFalse_WhenFileIsNotAZip()
    {
        var mockFile = Substitute.For<IFile>();
        mockFile.Exists("notazip.zip").Returns(true);
        mockFile.OpenRead("notazip.zip").Returns(new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip file")));
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFile);
            Assert.That(ZipFileWrapper.IsValidZipFile("notazip.zip"), Is.False);
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void IsValidZipFile_ReturnsTrue_WhenFileIsValidZip()
    {
        var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("test.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello");
        }
        zipStream.Position = 0;

        var mockFile = Substitute.For<IFile>();
        mockFile.Exists("valid.zip").Returns(true);
        mockFile.OpenRead("valid.zip").Returns(zipStream);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFile);
            Assert.That(ZipFileWrapper.IsValidZipFile("valid.zip"), Is.True);
            FactoryContainer.Clear();
        }
    }
}
