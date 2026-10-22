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

// A CREATE OR ALTER script is a syntax error on any SQL Server build before 2016 SP1, so a package whose declared floor
// admits such a build is told once, naming the scripts, before a deploy finds out.
[TestFixture]
public class ModuleSyntaxCheckTests
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

    private static ValidationContext Context(Platform platform, string minimumVersion) =>
        new(new Product { Name = "Acme", Platform = platform, MinimumVersion = minimumVersion }, new List<Template>(), PackagePath);

    private string Script(string relative, string content)
    {
        var path = Path.Join(PackagePath, relative);
        _mockFile.Exists(path).Returns(true);
        _mockFile.ReadAllText(path).Returns(content);
        return path;
    }

    private void PackageHas(params string[] paths) =>
        _mockDirectory.GetFiles(PackagePath, "*.sql", SearchOption.AllDirectories).Returns(paths);

    [TestCase("2016")]
    [TestCase("2008")]
    [TestCase("13")]
    public void AFloorBelow2017_WithCreateOrAlterScripts_IsOneWarningNamingThem(string minimumVersion)
    {
        var proc = Script(Path.Join("Templates", "Main", "Procedures", "dbo.p.sql"), "CREATE OR ALTER PROCEDURE dbo.p AS RETURN");
        var view = Script(Path.Join("Templates", "Main", "Views", "dbo.v.sql"), "create or\nalter VIEW dbo.v AS SELECT 1 AS x");
        var legacy = Script(Path.Join("Templates", "Main", "Views", "dbo.w.sql"),
            "IF OBJECT_ID(N'[dbo].[w]') IS NULL EXEC('CREATE VIEW [dbo].[w] AS SELECT 1 AS x')\r\nGO\r\nALTER VIEW [dbo].[w] AS SELECT 2 AS x");
        PackageHas(proc, view, legacy);

        var findings = new ModuleSyntaxCheck().Run(Context(Platform.SqlServer, minimumVersion)).ToList();

        Assert.That(findings, Has.Exactly(1).Items, "one finding per package, not per script");
        var finding = findings[0];
        Assert.Multiple(() =>
        {
            Assert.That(finding.Code, Is.EqualTo("SS-VER-002"));
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Location, Is.EqualTo("Product 'Acme'"));
            Assert.That(finding.Message, Does.Contain($"MinimumVersion '{minimumVersion}'"));
            Assert.That(finding.Message, Does.Contain("2 scripts use it (Templates/Main/Procedures/dbo.p.sql, Templates/Main/Views/dbo.v.sql)"));
            Assert.That(finding.Message, Does.Not.Contain("dbo.w.sql"), "the legacy form is what the warning recommends, so it is not an offender");
            Assert.That(finding.Message, Does.Contain("2017").And.Contain("ShouldCast:ModuleSyntax").And.Contain("Legacy"));
        });
    }

    [Test]
    public void MoreThanThreeScripts_NamesTheFirstThreeAndTheCount()
    {
        PackageHas(Enumerable.Range(1, 5).Select(i => Script(Path.Join("Procedures", $"dbo.p{i}.sql"), "CREATE OR ALTER PROCEDURE dbo.p AS RETURN")).ToArray());

        var finding = new ModuleSyntaxCheck().Run(Context(Platform.SqlServer, "2016")).Single();

        Assert.That(finding.Message, Does.Contain("5 scripts use it (Procedures/dbo.p1.sql, Procedures/dbo.p2.sql, Procedures/dbo.p3.sql, ...)"));
    }

    [Test]
    public void OneScript_IsSingular()
    {
        PackageHas(Script(Path.Join("Procedures", "dbo.p.sql"), "CREATE OR ALTER PROCEDURE dbo.p AS RETURN"));

        var finding = new ModuleSyntaxCheck().Run(Context(Platform.SqlServer, "2014")).Single();

        Assert.That(finding.Message, Does.Contain("1 script uses it (Procedures/dbo.p.sql)"));
    }

    [TestCase(Platform.SqlServer, "2017", TestName = "AFloorOf2017_IsClean")]
    [TestCase(Platform.SqlServer, "14", TestName = "AMajorOf14_IsClean")]
    [TestCase(Platform.SqlServer, "2022", TestName = "AFloorOf2022_IsClean")]
    [TestCase(Platform.SqlServer, "", TestName = "NoDeclaredFloor_MakesNoClaim_IsClean")]
    [TestCase(Platform.SqlServer, null, TestName = "ANullFloor_MakesNoClaim_IsClean")]
    [TestCase(Platform.SqlServer, "2018", TestName = "AnUnresolvableFloor_IsLeftToSSVER001")]
    [TestCase(Platform.PostgreSQL, "12", TestName = "AnotherEngine_IsClean")]
    public void OtherPackages_AreClean(Platform platform, string minimumVersion)
    {
        PackageHas(Script(Path.Join("Procedures", "dbo.p.sql"), "CREATE OR ALTER PROCEDURE dbo.p AS RETURN"));

        Assert.That(new ModuleSyntaxCheck().Run(Context(platform, minimumVersion)), Is.Empty);
    }

    [Test]
    public void AFloorBelow2017_WhoseScriptsNeverUseCreateOrAlter_IsClean()
    {
        PackageHas(
            Script(Path.Join("Procedures", "dbo.p.sql"), "CREATE PROCEDURE dbo.p AS RETURN"),
            Script(Path.Join("Views", "dbo.v.sql"), "-- CREATE OR ALTERNATIVE is not the statement\r\nCREATE VIEW dbo.v AS SELECT 1 AS x"));

        Assert.That(new ModuleSyntaxCheck().Run(Context(Platform.SqlServer, "2016")), Is.Empty);
    }

    [Test]
    public void TheRegistryRunsIt() =>
        Assert.That(ValidationCheckRegistry.Default().OfType<ModuleSyntaxCheck>(), Has.Exactly(1).Items);
}
