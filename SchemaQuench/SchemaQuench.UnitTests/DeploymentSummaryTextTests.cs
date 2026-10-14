// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>
/// E3: proves <see cref="DeploymentSummaryText.Render"/> — the HUMAN channel twin of E2's JSON —
/// renders the required sections and, critically, pins the conditional section behaviors from the
/// plan's E3 test matrix (sections that appear/disappear based on failures/WhatIf/bottlenecks
/// presence, and the objectChanges instrumented-vs-not branch). Assertions target structural
/// substrings/section presence rather than exact full-line prose, so tests aren't brittle to
/// wording tweaks — except the literal <c>(not instrumented)</c> marker, which is pinned exactly.
/// </summary>
[TestFixture]
public class DeploymentSummaryTextTests
{
    private static DeploymentSummary BuildFullyPopulatedSummary()
    {
        var run = new RunInfo(
            Product: "MyProduct",
            Platform: "PostgreSQL",
            StartedUtc: new DateTime(2026, 7, 8, 14, 22, 3, DateTimeKind.Utc),
            FinishedUtc: new DateTime(2026, 7, 8, 14, 23, 31, DateTimeKind.Utc),
            DurationMs: 88214,
            Mode: RunMode.Quench,
            Outcome: RunOutcome.PartialFailure,
            ExitCode: 2,
            ResumedFromCheckpoint: false);

        var targets = new List<TargetSummary>
        {
            new(
                Server: "primary",
                Database: "TenantA",
                Schema: "sales",
                Template: "TenantSchema",
                Outcome: TargetOutcome.Success,
                DurationMs: 12044,
                Slots: new List<TargetSlotTiming> { new("ModifiedTables", 8021, 0) }),
            new(
                Server: "primary",
                Database: "TenantB",
                Schema: null,
                Template: "TenantDatabase",
                Outcome: TargetOutcome.Failed,
                DurationMs: 3000,
                Slots: new List<TargetSlotTiming>())
        };

        var migrationScripts = new List<MigrationScriptRecord>
        {
            new(
                Path: "After Scripts/01_seed.sql",
                Slot: "After",
                Template: "TenantSchema",
                Schema: "sales",
                Server: "primary",
                Database: "TenantA",
                Outcome: "Ran")
        };

        var timing = new TimingSummary(
            TotalMs: 88214,
            BySlot: new List<SlotTiming> { new("ModifiedTables", 41230, 5) },
            ByDatabase: new List<DbTiming> { new("TenantA", 12044) },
            Bottlenecks: new List<BottleneckEntry> { new("primary/TenantA [sales]", "ModifiedTables", 8021) });

        var failures = new List<FailureRecord>
        {
            new(
                Phase: "AfterScripts",
                ScopeKey: "[primary]",
                Error: "boom",
                ContextTail: new List<string> { "line 1", "line 2" },
                ArtifactPath: "SchemaQuench - After primary.public.sql")
        };

        var whatIf = new WhatIfSummary(
            WouldApply: new List<WhatIfEntry> { new("primary/TenantA", "ALTER TABLE ...") },
            WouldSkip: new List<WhatIfEntry>(),
            WouldDeliver: new List<WhatIfEntry>());

        var objectChanges = new ObjectChangeSummary(
            Instrumented: false,
            Created: new CreatedCounts(0, 0, 0, 0, 0, 0, 0, 0),
            Modified: new ModifiedCounts(0, 0),
            Dropped: new DroppedCounts(0, 0, 0, 0),
            ScriptsRan: 0,
            Details: new List<ObjectChangeDetail>());

        return new DeploymentSummary(
            SchemaVersion: "1.0",
            Tool: "SchemaQuench",
            ToolVersion: "2.3.0",
            Run: run,
            Targets: targets,
            MigrationScripts: migrationScripts,
            Timing: timing,
            Failures: failures,
            WhatIf: whatIf,
            ObjectChanges: objectChanges);
    }

    // ─── Targets: 0 / 1 / N ─────────────────────────────────────────────────────

    [Test]
    public void Render_ZeroTargets_DoesNotThrow_AndShowsZeroState()
    {
        var summary = BuildFullyPopulatedSummary() with { Targets = Array.Empty<TargetSummary>() };

        string result = null;
        Assert.DoesNotThrow(() => result = DeploymentSummaryText.Render(summary));

        Assert.That(result, Does.Contain("Targets (0)"));
        Assert.That(result, Does.Contain("no targets"));
    }

    [Test]
    public void Render_OneTarget_ShowsScopeAndOutcome()
    {
        var single = new List<TargetSummary>
        {
            new(
                Server: "primary",
                Database: "TenantA",
                Schema: "sales",
                Template: "TenantSchema",
                Outcome: TargetOutcome.Success,
                DurationMs: 12044,
                Slots: new List<TargetSlotTiming>())
        };
        var summary = BuildFullyPopulatedSummary() with { Targets = single };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("primary / TenantA [sales]"));
        Assert.That(result, Does.Contain("Success"));
    }

    [Test]
    public void Render_MultipleTargets_ShowsAllDistinctScopes()
    {
        var summary = BuildFullyPopulatedSummary();

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("primary / TenantA [sales]"));
        Assert.That(result, Does.Contain("primary / TenantB"));
    }

    [Test]
    public void Render_NullSchemaTarget_RendersScopeWithoutNullOrEmptyBrackets()
    {
        var summary = BuildFullyPopulatedSummary();

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("primary / TenantB"));
        Assert.That(result, Does.Not.Contain("TenantB [null]"));
        Assert.That(result, Does.Not.Contain("TenantB []"));
    }

    // ─── Failures: present / absent ─────────────────────────────────────────────

    [Test]
    public void Render_WithFailures_ShowsFailuresSectionAndErrorText()
    {
        var summary = BuildFullyPopulatedSummary();

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("Failures"));
        Assert.That(result, Does.Contain("boom"));
    }

    [Test]
    public void Render_WithoutFailures_OmitsFailuresSection()
    {
        var summary = BuildFullyPopulatedSummary() with { Failures = Array.Empty<FailureRecord>() };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Not.Contain("Failures"));
    }

    // ─── WhatIf: present only when WhatIf != null ───────────────────────────────

    [Test]
    public void Render_WhatIfMode_ShowsWhatIfSection()
    {
        var summary = BuildFullyPopulatedSummary() with
        {
            Run = BuildFullyPopulatedSummary().Run with { Mode = RunMode.WhatIf }
        };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("WhatIf"));
    }

    [Test]
    public void Render_QuenchMode_WhatIfNull_OmitsWhatIfSection()
    {
        var summary = BuildFullyPopulatedSummary() with { WhatIf = null };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Not.Contain("WhatIf"));
    }

    [Test]
    public void Render_WithUnsupportedDowngrade_ShowsDowngradeSection()
    {
        var summary = BuildFullyPopulatedSummary() with
        {
            UnsupportedDowngrade = new UnsupportedDowngradeSummary(new[]
            {
                new UnsupportedDowngradeEntry("NULLS NOT DISTINCT (PG15)", "public.t.uq_t")
            })
        };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("Unsupported Feature Downgrades"));
        Assert.That(result, Does.Contain("NULLS NOT DISTINCT (PG15): public.t.uq_t"));
    }

    [Test]
    public void Render_WithoutUnsupportedDowngrade_OmitsDowngradeSection()
    {
        var summary = BuildFullyPopulatedSummary() with { UnsupportedDowngrade = null };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Not.Contain("Unsupported Feature Downgrades"));
    }

    // ─── Bottlenecks: present only when non-empty ───────────────────────────────

    [Test]
    public void Render_BottlenecksNonEmpty_ShowsBottlenecksSubsection()
    {
        var summary = BuildFullyPopulatedSummary();

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("Bottlenecks"));
    }

    [Test]
    public void Render_BottlenecksEmpty_OmitsBottlenecksSubsection()
    {
        var summary = BuildFullyPopulatedSummary();
        summary = summary with { Timing = summary.Timing with { Bottlenecks = Array.Empty<BottleneckEntry>() } };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Not.Contain("Bottlenecks"));
    }

    // ─── Object changes: instrumented false / true ──────────────────────────────

    [Test]
    public void Render_ObjectChangesNotInstrumented_ShowsExactNotInstrumentedMarker()
    {
        var summary = BuildFullyPopulatedSummary();
        Assert.That(summary.ObjectChanges.Instrumented, Is.False, "fixture precondition");

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("(not instrumented)"));
    }

    [Test]
    public void Render_ObjectChangesInstrumented_ShowsRealCounts_NotNotInstrumentedMarker()
    {
        var summary = BuildFullyPopulatedSummary();
        summary = summary with
        {
            ObjectChanges = summary.ObjectChanges with
            {
                Instrumented = true,
                Created = new CreatedCounts(Tables: 3, Columns: 0, Indexes: 1, Constraints: 0, ForeignKeys: 0, Procedures: 0, Views: 0, Functions: 0),
                ScriptsRan = 2,
                ScriptsUnchanged = 5
            }
        };

        var result = DeploymentSummaryText.Render(summary);

        // "tables=3" (not a bare "3") so this doesn't accidentally match the fixture's unrelated
        // "3000ms" target duration — pins the actual created-tables count being rendered.
        Assert.That(result, Does.Contain("tables=3"));
        Assert.That(result, Does.Contain("Ran (object scripts): 2"));
        Assert.That(result, Does.Contain("Unchanged (object scripts): 5"));
        Assert.That(result, Does.Not.Contain("(not instrumented)"));
    }

    // ─── Scope formatting: product-folder scripts have no database ───────────────

    [Test]
    public void Render_ScriptWithEmptyDatabase_RendersServerOnly_NoDanglingSlash()
    {
        // Product-folder migration scripts are server-scoped — no template/schema/database. The
        // scope must collapse to just the server, not "primary / " with a dangling separator.
        var productScript = new List<MigrationScriptRecord>
        {
            new(Path: "Jobs/Job 1.sql", Slot: "Before Product", Template: "",
                Schema: null, Server: "primary", Database: null, Outcome: "Ran")
        };
        var summary = BuildFullyPopulatedSummary() with { MigrationScripts = productScript };

        var result = DeploymentSummaryText.Render(summary);

        Assert.That(result, Does.Contain("Jobs/Job 1.sql — primary"));
        Assert.That(result, Does.Not.Contain("Jobs/Job 1.sql — primary / "));
    }
}
