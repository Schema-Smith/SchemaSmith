// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Validation;

namespace Schema.UnitTests.Validation;

[TestFixture]
public class SchemaPackageValidatorTests
{
    [Test]
    public void Validate_LoadThrows_ReportsMalformedLoadError_NoCrash()
    {
        var validator = new SchemaPackageValidator(
            loader: () => throw new Exception("Error loading table from Tables/Bad.json\r\nUnexpected token"),
            checks: Array.Empty<ISchemaCheck>());
        var result = validator.Validate("pkg");
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Findings, Has.Exactly(1).Items);
        Assert.That(result.Findings[0].Category, Is.EqualTo("Load"));
        Assert.That(result.Findings[0].Code, Is.EqualTo("SS-LOAD-001"));
        Assert.That(result.Findings[0].Message, Does.Contain("Bad.json"));
    }

    [Test]
    public void Validate_RunsAllChecks_AggregatesFindings()
    {
        var pkg = ValidationTestPackages.Minimal(Platform.SqlServer);
        var check = Substitute.For<ISchemaCheck>();
        check.Run(Arg.Any<ValidationContext>())
             .Returns(new[] { new Finding(Severity.Error, "X", "Cat", "loc", "msg") });
        var result = new SchemaPackageValidator(() => pkg, new[] { check }).Validate("pkg");
        Assert.That(result.Findings, Has.Exactly(1).Items);
        check.Received(1).Run(Arg.Any<ValidationContext>());
    }

    // A Product.json with no Platform used to reach JsonSchemaCheck, which asks GetBasePlatform which
    // engine to use and gets an ArgumentException. Checks deliberately have no per-check try/catch, so that
    // surfaced to the operator as a raw stack trace rather than a finding -- on a directory that simply
    // was not a package.
    [Test]
    public void Validate_ProductWithoutPlatform_ReportsAFindingInsteadOfThrowing()
    {
        var pkg = ValidationTestPackages.Minimal(Platform.SqlServer);
        pkg.Product.Platform = Platform.Unknown;
        var check = Substitute.For<ISchemaCheck>();

        var result = new SchemaPackageValidator(() => pkg, new[] { check }).Validate("pkg");

        Assert.Multiple(() =>
        {
            Assert.That(result.HasErrors, Is.True);
            Assert.That(result.Findings, Has.Exactly(1).Items);
            Assert.That(result.Findings[0].Code, Is.EqualTo("SS-LOAD-003"));
            Assert.That(result.Findings[0].Message, Does.Contain("Platform"));
            // No check may run: each one would be asking the same unanswerable question.
            check.DidNotReceive().Run(Arg.Any<ValidationContext>());
        });
    }

    // A component that is valid JSON but failed to load is reported only when no check names the file:
    // a schema violation already has its precise finding, a converter-only rule has nothing else.
    [Test]
    public void Validate_ParseableLoadError_ReportedOnlyWhenNoCheckNamesTheFile()
    {
        var basePkg = ValidationTestPackages.Minimal(Platform.SqlServer);
        var pkg = basePkg with
        {
            UnlessNamedLoadFindings = new[]
            {
                new Finding(Severity.Error, "SS-LOAD-001", "Load", "Tables/Named.json", "named"),
                new Finding(Severity.Error, "SS-LOAD-001", "Load", "Tables/Unnamed.json", "unnamed"),
            }
        };
        var check = Substitute.For<ISchemaCheck>();
        check.Run(Arg.Any<ValidationContext>())
             .Returns(new[] { new Finding(Severity.Error, "SS-JSON-001", "Json", "Tables/Named.json", "schema") });

        var result = new SchemaPackageValidator(() => pkg, new[] { check }).Validate("pkg");

        Assert.That(result.Findings.Select(f => $"{f.Code} {f.Location}"), Is.EquivalentTo(new[]
        {
            "SS-JSON-001 Tables/Named.json",
            "SS-LOAD-001 Tables/Unnamed.json",
        }));
    }

    [Test]
    public void ValidationContext_AllTables_FlattensTemplates()
    {
        var pkg = ValidationTestPackages.Minimal(Platform.SqlServer);
        var ctx = new ValidationContext(pkg.Product, pkg.Templates, "pkg");
        Assert.That(ctx.AllTables, Is.Not.Empty);
        Assert.That(ctx.Platform, Is.EqualTo(Platform.SqlServer));
    }
}
