// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

[TestFixture]
public class SequenceScriptCheckTests
{
    private const string PackagePath = @"C:\pkg";

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
        new(new Product { Name = "Acme", Platform = Platform.MariaDb }, new List<Template>(), PackagePath);

    private string Script(string relative, string content)
    {
        var path = Path.Join(PackagePath, relative);
        _mockFile.Exists(path).Returns(true);
        _mockFile.ReadAllText(path).Returns(content);
        return path;
    }

    [TestCase("CREATE OR REPLACE SEQUENCE `s` start with 1")]
    [TestCase("create  or\nreplace sequence s")]
    public void ASequenceScriptThatReplaces_IsAWarning(string content)
    {
        var path = Script(Path.Join("Templates", "Main", "Sequences", "s.sql"), content);
        _mockDirectory.GetFiles(PackagePath, "*.sql", SearchOption.AllDirectories).Returns(new[] { path });

        var findings = new SequenceScriptCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-SEQ-002"));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(findings[0].Location, Is.EqualTo(path));
        Assert.That(findings[0].Message, Does.Contain("IF NOT EXISTS"));
    }

    [TestCase("Sequences", "CREATE SEQUENCE IF NOT EXISTS `s` start with 1", TestName = "IfNotExists_IsClean")]
    [TestCase("MigrationScripts", "CREATE OR REPLACE SEQUENCE `s`", TestName = "AMigrationScriptRunsOnce_IsClean")]
    public void OtherScripts_AreClean(string folder, string content)
    {
        var path = Script(Path.Join("Templates", "Main", folder, "x.sql"), content);
        _mockDirectory.GetFiles(PackagePath, "*.sql", SearchOption.AllDirectories).Returns(new[] { path });

        var findings = new SequenceScriptCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }
}
