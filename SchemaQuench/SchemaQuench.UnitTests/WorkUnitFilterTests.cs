// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Slice-5 selective execution filter (design §9.3–§9.4, §9.7). Verifies the
/// orthogonal cross-product filter, the empty-result diagnostic, the unknown-value
/// diagnostic with available-options listing, and the "regular templates bypass
/// Target.Schemas" rule (with warning).
/// </summary>
[TestFixture]
public class WorkUnitFilterTests
{
    private static readonly WorkUnit SharedDbA = new("primary", "AppA", "Shared", "");
    private static readonly WorkUnit SharedDbB = new("primary", "AppB", "Shared", "");
    private static readonly WorkUnit TenantBodyAcmeA = new("primary", "AppA", "TenantBody", "tenant_acme");
    private static readonly WorkUnit TenantBodyGlobexA = new("primary", "AppA", "TenantBody", "tenant_globex");
    private static readonly WorkUnit TenantBodyAcmeB = new("primary", "AppB", "TenantBody", "tenant_acme");
    private static readonly WorkUnit TenantBodyGlobexB = new("primary", "AppB", "TenantBody", "tenant_globex");

    private static List<WorkUnit> AllSixUnits() =>
    [
        SharedDbA, SharedDbB,
        TenantBodyAcmeA, TenantBodyGlobexA,
        TenantBodyAcmeB, TenantBodyGlobexB
    ];

    [Test]
    public void TemplateNameMatching_IsCaseInsensitive()
    {
        // Template-name comparisons across the validator + filter trio are OrdinalIgnoreCase.
        // A lowercase Target.Templates filter value still matches the canonical "TenantBody"
        // template name discovered by EnumerateWorkUnitsForTemplate.
        var filter = new WorkUnitFilter(["tenantbody"], [], []);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.That(result.All(u => u.TemplateName == "TenantBody"), Is.True);
        Assert.That(result, Has.Count.EqualTo(4));
    }

    [Test]
    public void Rule6_FilterOutsideOverrideUniverse_FailsWithUnknownValueDiagnostic()
    {
        // #257 design §5 rule 6: when TemplateTargets has replaced the discovered universe with
        // a config-driven override (e.g., Schemas=[acme,globex]), a Target.Schemas filter value
        // not in that override universe must surface a precise "value not discovered" diagnostic.
        // The override has REPLACED discovery — the override list IS the discovered universe at
        // this layer, so the existing rule-6 unknown-value path naturally catches the mismatch
        // without any extra filter logic.
        var overrideUnits = new List<WorkUnit>
        {
            new("primary", "appdb", "TenantSchema", "acme"),
            new("primary", "appdb", "TenantSchema", "globex")
        };
        var filter = new WorkUnitFilter([], [], ["acme", "tenant_not_in_override"]);

        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(overrideUnits, warn: _ => { }));

        Assert.That(ex!.Message,
            Does.Contain("tenant_not_in_override")
            .And.Contain("acme,globex"));
    }

    [Test]
    public void EmptyFilters_AllUnitsPassThrough()
    {
        var filter = new WorkUnitFilter(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.That(result, Is.EquivalentTo(AllSixUnits()));
    }

    [Test]
    public void NullFilters_AllUnitsPassThrough()
    {
        // Defensive: null arrays normalize to empty internally; this guards against the constructor
        // forcing callers into Array.Empty<string>() boilerplate when a config-binder returns null.
        var filter = new WorkUnitFilter(null, null, null);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.That(result, Is.EquivalentTo(AllSixUnits()));
    }

    [Test]
    public void TargetTemplates_FiltersToNamedTemplate_DropsOtherTemplates()
    {
        var filter = new WorkUnitFilter(["TenantBody"], [], []);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(u => u.TemplateName == "TenantBody"), Is.True);
            Assert.That(result, Does.Not.Contain(SharedDbA));
            Assert.That(result, Does.Not.Contain(SharedDbB));
        });
    }

    [Test]
    public void TargetDatabases_FiltersToNamedDatabase_DropsOtherDatabases()
    {
        var filter = new WorkUnitFilter([], ["AppA"], []);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result.All(u => u.DatabaseName == "AppA"), Is.True);
        });
    }

    [Test]
    public void TargetSchemas_FiltersToNamedSchema_KeepsRegularTemplateUnitsUnaffected()
    {
        // Regular-template units carry SchemaName == "" and bypass the schema filter (the §9.3
        // expression `string.IsNullOrEmpty(u.SchemaName) || _schemas.Contains(u.SchemaName)`).
        var filter = new WorkUnitFilter([], [], ["tenant_acme"]);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.Multiple(() =>
        {
            // Regular-template units (Shared) still present.
            Assert.That(result, Does.Contain(SharedDbA));
            Assert.That(result, Does.Contain(SharedDbB));
            // Only tenant_acme survives among schema-template units.
            Assert.That(result.Where(u => u.TemplateName == "TenantBody").Select(u => u.SchemaName),
                Is.All.EqualTo("tenant_acme"));
            Assert.That(result.Count(u => u.SchemaName == "tenant_acme"), Is.EqualTo(2));
        });
    }

    [Test]
    public void TargetSchemas_OnAllRegularUnits_LogsWarningOnce()
    {
        // The unit list has no schema-template units, so Target.Schemas can't take effect.
        // Per §9.4, log a warning and return the units unaffected.
        var warnings = new List<string>();
        var filter = new WorkUnitFilter([], [], ["tenant_acme"]);
        var regularOnly = new List<WorkUnit> { SharedDbA, SharedDbB };

        var result = filter.Apply(regularOnly, warn: warnings.Add);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EquivalentTo(regularOnly));
            Assert.That(warnings, Has.Count.EqualTo(1),
                "Target.Schemas with no schema-template units must log exactly one warning per Apply.");
            Assert.That(warnings[0], Does.Contain("Target.Schemas"));
            Assert.That(warnings[0], Does.Contain("regular"));
        });
    }

    [Test]
    public void CrossProduct_TemplatesAndSchemas_AppliesIntersection()
    {
        var filter = new WorkUnitFilter(["TenantBody"], [], ["tenant_acme"]);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.All(u => u.TemplateName == "TenantBody" && u.SchemaName == "tenant_acme"), Is.True);
            Assert.That(result.Select(u => u.DatabaseName), Is.EquivalentTo(new[] { "AppA", "AppB" }));
        });
    }

    [Test]
    public void CrossProduct_AllThreeDimensions_NarrowsToSingleUnit()
    {
        var filter = new WorkUnitFilter(["TenantBody"], ["AppA"], ["tenant_acme"]);

        var result = filter.Apply(AllSixUnits(), warn: _ => { });

        Assert.That(result, Is.EqualTo(new[] { TenantBodyAcmeA }));
    }

    [Test]
    public void EmptyResult_AfterFiltering_ThrowsWithDiagnostic()
    {
        var filter = new WorkUnitFilter(["TenantBody"], ["AppA"], ["tenant_nonexistent"]);

        // tenant_nonexistent isn't a known schema → the unknown-value path fires first with the
        // available-options diagnostic; the zero-result path is exercised separately below.
        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(AllSixUnits(), warn: _ => { }));
        Assert.That(ex.Message, Does.Contain("tenant_nonexistent"));
    }

    [Test]
    public void EmptyResult_AllNamesKnown_DoesNotHappenWithKnownValues()
    {
        // When every filter value IS in the discovered set, the cross-product still narrows
        // correctly. This test guards against an accidentally-too-aggressive "filters are
        // inconsistent" error firing on a legitimate single-tuple selection.
        var filter = new WorkUnitFilter(["TenantBody"], ["AppA"], ["tenant_acme"]);

        Assert.That(filter.Apply(AllSixUnits(), warn: _ => { }), Has.Count.EqualTo(1));
    }

    [Test]
    public void UnknownTemplateName_ThrowsWithAvailableOptions()
    {
        var filter = new WorkUnitFilter(["NoSuchTemplate"], [], []);

        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(AllSixUnits(), warn: _ => { }));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("NoSuchTemplate"));
            // Available options listing must enumerate the discovered templates so the user can
            // copy-paste the right value.
            Assert.That(ex.Message, Does.Contain("Shared"));
            Assert.That(ex.Message, Does.Contain("TenantBody"));
            Assert.That(ex.Message, Does.Contain("Target.Templates"));
        });
    }

    [Test]
    public void UnknownDatabaseName_ThrowsWithAvailableOptions()
    {
        var filter = new WorkUnitFilter([], ["AppZ"], []);

        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(AllSixUnits(), warn: _ => { }));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("AppZ"));
            Assert.That(ex.Message, Does.Contain("AppA"));
            Assert.That(ex.Message, Does.Contain("AppB"));
            Assert.That(ex.Message, Does.Contain("Target.Databases"));
        });
    }

    [Test]
    public void UnknownSchemaName_ThrowsWithAvailableOptions()
    {
        var filter = new WorkUnitFilter([], [], ["tenant_unknown"]);

        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(AllSixUnits(), warn: _ => { }));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("tenant_unknown"));
            Assert.That(ex.Message, Does.Contain("tenant_acme"));
            Assert.That(ex.Message, Does.Contain("tenant_globex"));
            Assert.That(ex.Message, Does.Contain("Target.Schemas"));
        });
    }

    [Test]
    public void UnknownNames_AcrossMultipleDimensions_AllListedInOneError()
    {
        // Per-missing-name detail (§9.4) — a single Apply call should report ALL the user's bad
        // values together, not stop at the first. Saves a round-trip if multiple were typos.
        var filter = new WorkUnitFilter(["NoSuchTemplate"], ["NoSuchDb"], []);

        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(AllSixUnits(), warn: _ => { }));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("NoSuchTemplate"));
            Assert.That(ex.Message, Does.Contain("NoSuchDb"));
        });
    }

    [Test]
    public void EmptyDiscovery_WithEmptyFilters_ThrowsZeroResult()
    {
        // Discovery returned nothing — that's a real condition (no DBs match the identification
        // script). The filter's empty-result diagnostic still fires so callers know why there's
        // nothing to do.
        var filter = new WorkUnitFilter([], [], []);

        var ex = Assert.Throws<InvalidOperationException>(() => filter.Apply(new List<WorkUnit>(), warn: _ => { }));
        Assert.That(ex!.Message, Does.Contain("zero work units"));
    }

    [Test]
    public void DatabaseFilter_OnAServerThatFoldsNames_MatchesTheCatalogSpelling()
    {
        // A MySQL/MariaDB server with lower_case_table_names >= 1 reports TestMain as testmain, so the configured
        // spelling must still select it there.
        var discovered = new List<WorkUnit> { new("primary", "testmain", "Main", ""), new("primary", "other", "Main", "") };
        var filter = new WorkUnitFilter([], ["TestMain"], [], databaseNamesFold: true);

        var result = filter.Apply(discovered, warn: _ => { });

        Assert.That(result.Select(u => u.DatabaseName), Is.EqualTo(new[] { "testmain" }));
    }

    [Test]
    public void DatabaseFilter_OnACaseSensitiveServer_SelectsOnlyTheExactSpelling()
    {
        var discovered = new List<WorkUnit> { new("primary", "TestMain", "Main", ""), new("primary", "testmain", "Main", "") };
        var filter = new WorkUnitFilter([], ["testmain"], [], databaseNamesFold: false);

        var result = filter.Apply(discovered, warn: _ => { });

        Assert.That(result.Select(u => u.DatabaseName), Is.EqualTo(new[] { "testmain" }));
    }
}
