// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

[TestFixture]
public class GeneratedScriptCheckTests
{
    private const string PackagePath = @"C:\pkg";
    private const string Body = "IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE [name] = N'CMK')\r\n  EXEC(N'CREATE COLUMN MASTER KEY [CMK] ...')\r\n";

    private IFile _mockFile;
    private IDirectory _mockDirectory;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _mockFile = Substitute.For<IFile>();
        _mockDirectory = Substitute.For<IDirectory>();
        FactoryContainer.Register(_mockFile);
        FactoryContainer.Register(_mockDirectory);
        _mockDirectory.Exists(PackagePath).Returns(true);
    }

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static ValidationContext Context() =>
        new(new Product { Name = "Acme", Platform = Platform.SqlServer }, new List<Template>(), PackagePath);

    private List<Finding> Check(string content)
    {
        var path = Path.Join(PackagePath, "Templates", "Main", "Prerequisites", "AlwaysEncryptedKeys.sql");
        _mockFile.Exists(path).Returns(true);
        _mockFile.ReadAllText(path).Returns(content);
        _mockDirectory.GetFiles(PackagePath, "*.sql", SearchOption.AllDirectories).Returns(new[] { path });
        return new GeneratedScriptCheck().Run(Context()).ToList();
    }

    [Test]
    public void AGeneratedScriptAsWritten_IsClean_WhateverItsLineEndings()
    {
        var stamped = GeneratedScript.Stamp(Body);
        Assert.Multiple(() =>
        {
            Assert.That(Check(stamped), Is.Empty);
            Assert.That(Check(stamped.Replace("\r\n", "\n")), Is.Empty, "a checkout that converts line endings is not an edit");
            Assert.That(Check(stamped.TrimEnd() + "  \r\n\r\n"), Is.Empty, "nor is trailing whitespace");
        });
    }

    [Test]
    public void AGeneratedScriptEditedByHand_IsAWarningSayingTheNextExtractionOverwritesIt()
    {
        var findings = Check(GeneratedScript.Stamp(Body).Replace("[CMK]", "[CMK2]"));
        Assert.That(findings, Has.Exactly(1).Items);
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Code, Is.EqualTo("SS-GEN-001"));
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
            Assert.That(findings[0].Message, Does.Contain("edited").And.Contain("overwrites"));
        });
    }

    [Test]
    public void AScriptWithoutTheMarker_IsTheUsersOwn_AndNotChecked()
    {
        Assert.That(Check(Body), Is.Empty);
    }

    [Test]
    public void AMarkerWithNothingAfterIt_IsAnEdit()
    {
        Assert.That(GeneratedScript.IsHandEdited(GeneratedScript.MarkerPrefix + "abc"), Is.True);
    }

    [Test]
    public void TheCheckIsRegistered()
    {
        Assert.That(ValidationCheckRegistry.Default().OfType<GeneratedScriptCheck>(), Has.Exactly(1).Items);
    }
}
