// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NSubstitute;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// SS-GEN-001 tells the user to remove the marker line to make a generated script their own, but SchemaTongs overwrote
/// the file on the next extraction whatever it carried. A file without the marker is the user's and is kept.
/// </summary>
[TestFixture]
public class GeneratedScriptTests
{
    private const string Path = "C:/pkg/Templates/Main/Prerequisites/AlwaysEncryptedKeys.sql";
    private IFile _file;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _file = Substitute.For<IFile>();
        FactoryContainer.Register(_file);
    }

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    [Test]
    public void ANewFile_IsWritten_Stamped()
    {
        _file.Exists(Path).Returns(false);
        Assert.That(GeneratedScript.WriteUnlessOwned(Path, "SELECT 1"), Is.True);
        _file.Received(1).WriteAllText(Path, GeneratedScript.Stamp("SELECT 1"));
    }

    [Test]
    public void AGeneratedFile_IsReplaced()
    {
        _file.Exists(Path).Returns(true);
        _file.ReadAllText(Path).Returns(GeneratedScript.Stamp("SELECT 0"));
        Assert.That(GeneratedScript.WriteUnlessOwned(Path, "SELECT 1"), Is.True);
        _file.Received(1).WriteAllText(Path, GeneratedScript.Stamp("SELECT 1"));
    }

    [Test]
    public void AFileWhoseMarkerWasRemoved_IsKept()
    {
        _file.Exists(Path).Returns(true);
        _file.ReadAllText(Path).Returns("-- mine now\r\nSELECT 2");
        Assert.That(GeneratedScript.WriteUnlessOwned(Path, "SELECT 1"), Is.False);
        _file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
    }
}
