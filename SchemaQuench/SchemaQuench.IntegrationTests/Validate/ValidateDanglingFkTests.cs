// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Schema.Validation;

namespace SchemaQuench.IntegrationTests.Validate;

/// <summary>
/// An FK whose RelatedTable is not declared in the package (<c>CoherenceCheck</c>, SS-FK-002), against a real
/// on-disk package. The deploy creates such a key whenever the table exists on the target, so it is a warning --
/// and none at all for a partial deployment, a package that keeps tables it does not declare.
/// </summary>
[TestFixture]
[Category("Validate")]
public class ValidateDanglingFkTests : ValidateFixtureTestBase
{
    [Test]
    public void Validate_DanglingFk_WarnsWithoutFailingTheRun()
    {
        var result = RunValidate(FixturePath("DanglingFk", "SqlServer"));

        var finding = result.Findings.Single(f => f.Code == "SS-FK-002");
        Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
        Assert.That(result.HasErrors, Is.False,
            $"a dangling FK alone must not fail --Validate: {string.Join("; ", result.Findings.Select(f => f.Code))}");
    }

    [Test]
    public void Validate_DanglingFk_IsSilent_ForAPartialDeployment()
    {
        var result = RunValidate(FixturePath("DanglingFkPartialDeployment", "SqlServer"));

        Assert.That(result.Findings.Select(f => f.Code), Has.None.EqualTo("SS-FK-002"));
        Assert.That(result.HasErrors, Is.False);
    }
}
