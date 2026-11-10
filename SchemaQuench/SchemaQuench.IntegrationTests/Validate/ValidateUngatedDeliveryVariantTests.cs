// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;

namespace SchemaQuench.IntegrationTests.Validate;

// Several deliveries on one table need a ShouldApplyExpression on each. The file is valid JSON and its
// schema accepts it, so only the loader's converter refuses it: the deploy stops on the file, and --Validate
// must name the file rather than pass clean.
[TestFixture]
[Category("Validate")]
public class ValidateUngatedDeliveryVariantTests : ValidateFixtureTestBase
{
    [Test]
    public void Validate_SeveralDeliveriesOneUngated_ReportsTheFile()
    {
        var result = RunValidate(FixturePath("UngatedDeliveryVariant", "SqlServer"));

        var named = result.Findings.Where(f => f.Location.EndsWith("dbo.Widget.json")).ToList();
        Assert.That(named, Is.Not.Empty, "no finding names dbo.Widget.json; findings: "
            + string.Join(" | ", result.Findings.Select(f => $"{f.Code} {f.Location}")));
        Assert.That(result.HasErrors, Is.True);
        Assert.That(named.Single().Code, Is.EqualTo("SS-LOAD-001"));
        Assert.That(named.Single().Message, Does.Contain("ShouldApplyExpression"));
    }
}
