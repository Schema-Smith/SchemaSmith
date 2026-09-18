// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Schema.Domain;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// A deprecated alias migrates at load and keeps the package working, so the only thing that says a
/// package still depends on it is the warning -- and that warning was a progress-log line, invisible to
/// --Validate, which reported such a package as a clean PASS. The alias migrations record what they did
/// on the template; this check reports it.
/// </summary>
[TestFixture]
public class DeprecationCheckTests
{
    private static ValidationContext Context(params Template[] templates) =>
        new(new Product { Name = "Acme", Platform = Platform.MySQL }, templates, "pkg");

    [Test]
    public void EachRecordedNotice_IsAWarningFinding_CarryingItsCodeAndLocation()
    {
        var main = new Template { Name = "Main" };
        main.DeprecationNotices.Add(new DeprecationNotice("SS-DEP-001", @"C:\pkg\Templates\Main\Template.json", "use DatabaseIdentificationScript"));
        var other = new Template { Name = "Other" };
        other.DeprecationNotices.Add(new DeprecationNotice("SS-DEP-002", "Template 'Other' / Table 'Orders'", "move it to CheckConstraints"));

        var findings = new DeprecationCheck().Run(Context(main, other)).ToList();

        Assert.That(findings.Select(f => (f.Severity, f.Code, f.Category, f.Location, f.Message)), Is.EqualTo(new[]
        {
            (Severity.Warning, "SS-DEP-001", "Deprecated", @"C:\pkg\Templates\Main\Template.json", "use DatabaseIdentificationScript"),
            (Severity.Warning, "SS-DEP-002", "Deprecated", "Template 'Other' / Table 'Orders'", "move it to CheckConstraints"),
        }));
    }

    [Test]
    public void NoNotices_NoFindings()
    {
        Assert.That(new DeprecationCheck().Run(Context(new Template { Name = "Main" })), Is.Empty);
    }

    [Test]
    public void IsRegisteredForValidate()
    {
        Assert.That(ValidationCheckRegistry.Default().OfType<DeprecationCheck>(), Has.Exactly(1).Items);
    }
}
