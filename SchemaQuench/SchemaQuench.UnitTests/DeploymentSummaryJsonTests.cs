// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>
/// E2: proves the DeploymentSummary record graph serializes to the frozen v1 JSON contract
/// (field names, camelCase keys, enum-as-string, null preservation). The paid Intelligence
/// Downstream consumers deserialize this JSON, so assertions parse the output with JObject/SelectToken rather
/// than substring-matching, and pin down the exact shapes a consumer would rely on.
/// </summary>
[TestFixture]
public class DeploymentSummaryJsonTests
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
                Slots: new List<TargetSlotTiming> { new("ModifiedTables", 8021, 0) },
                DatabaseSource: "DatabaseIdentificationScript",
                SchemaSource: "SchemaIdentificationScript"),
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

    [Test]
    public void Serialize_EmitsFrozenTopLevelIdentity()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        Assert.That(json.SelectToken("schemaVersion")?.Value<string>(), Is.EqualTo("1.0"));
        Assert.That(json.SelectToken("tool")?.Value<string>(), Is.EqualTo("SchemaQuench"));
    }

    [Test]
    public void Serialize_EmitsEveryTopLevelSection()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        Assert.That(json["run"], Is.Not.Null);
        Assert.That(json["targets"], Is.Not.Null);
        Assert.That(json["migrationScripts"], Is.Not.Null);
        Assert.That(json["timing"], Is.Not.Null);
        Assert.That(json["failures"], Is.Not.Null);
        Assert.That(json["whatIf"], Is.Not.Null);
        Assert.That(json["objectChanges"], Is.Not.Null);
    }

    [Test]
    public void Serialize_EmitsTheSourceDisclosureEachTargetAnnounced()
    {
        // The schemaVersion bump to 1.1 announces this shape; nothing asserted the shape itself, and
        // both fields default to "" -- so dropping the propagation from TargetResult would have produced
        // a well-formed report carrying two empty strings, with no null, no parse error and no red test.
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        Assert.Multiple(() =>
        {
            Assert.That(json.SelectToken("targets[0].databaseSource")?.Value<string>(),
                Is.EqualTo("DatabaseIdentificationScript"));
            Assert.That(json.SelectToken("targets[0].schemaSource")?.Value<string>(),
                Is.EqualTo("SchemaIdentificationScript"));
            // The second target sets neither, which is the ordinary case and must still emit the keys --
            // a consumer reading targets[].databaseSource should find a field, not a hole.
            Assert.That(json.SelectToken("targets[1].databaseSource"), Is.Not.Null);
            Assert.That(json.SelectToken("targets[1].schemaSource"), Is.Not.Null);
        });
    }

    [Test]
    public void Serialize_UsesStringEnumConverter_NotIntegers()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        Assert.That(json.SelectToken("run.mode")?.Value<string>(), Is.EqualTo("Quench"));
        Assert.That(json.SelectToken("run.outcome")?.Value<string>(), Is.EqualTo("PartialFailure"));
        Assert.That(json.SelectToken("targets[0].outcome")?.Value<string>(), Is.EqualTo("Success"));
        Assert.That(json.SelectToken("targets[1].outcome")?.Value<string>(), Is.EqualTo("Failed"));
    }

    [Test]
    public void Serialize_UsesCamelCaseKeys_NotPascalCase()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        Assert.That(json.SelectToken("run.durationMs"), Is.Not.Null);
        Assert.That(json.SelectToken("run.resumedFromCheckpoint"), Is.Not.Null);
        Assert.That(json.SelectToken("schemaVersion"), Is.Not.Null);

        Assert.That(json.SelectToken("run.DurationMs"), Is.Null);
        Assert.That(json.SelectToken("SchemaVersion"), Is.Null);
    }

    [Test]
    public void Serialize_EmitsObjectChangesInstrumentedMarker_EvenWhenFalse()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        var instrumented = json.SelectToken("objectChanges.instrumented");
        Assert.That(instrumented, Is.Not.Null);
        Assert.That(instrumented.Value<bool>(), Is.False);
    }

    [Test]
    public void Serialize_ProjectsFailureRecordFieldsUnderCamelCaseResolver()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        var failure = json.SelectToken("failures[0]");
        Assert.That(failure, Is.Not.Null);
        Assert.That(failure.SelectToken("phase")?.Value<string>(), Is.EqualTo("AfterScripts"));
        Assert.That(failure.SelectToken("scopeKey")?.Value<string>(), Is.EqualTo("[primary]"));
        Assert.That(failure.SelectToken("error")?.Value<string>(), Is.EqualTo("boom"));
        Assert.That(failure.SelectToken("contextTail")?.Type, Is.EqualTo(JTokenType.Array));
        Assert.That(failure.SelectToken("contextTail")?.Values<string>(), Is.EqualTo(new[] { "line 1", "line 2" }));
        Assert.That(failure.SelectToken("artifactPath")?.Value<string>(), Is.EqualTo("SchemaQuench - After primary.public.sql"));
    }

    [Test]
    public void Serialize_EmitsNullSchemaAsJsonNull_ForDbLevelTarget()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        var schemaToken = json.SelectToken("targets[1].schema");
        Assert.That(schemaToken, Is.Not.Null);
        Assert.That(schemaToken.Type, Is.EqualTo(JTokenType.Null));
    }

    [Test]
    public void Serialize_ProjectsE1SlotTimingFieldsUnderTiming()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        var slot = json.SelectToken("timing.bySlot[0]");
        Assert.That(slot, Is.Not.Null);
        Assert.That(slot.SelectToken("slot")?.Value<string>(), Is.EqualTo("ModifiedTables"));
        Assert.That(slot.SelectToken("totalMs")?.Value<long>(), Is.EqualTo(41230));
        Assert.That(slot.SelectToken("targetCount")?.Value<int>(), Is.EqualTo(5));
    }

    [Test]
    public void Serialize_EmitsPerTargetSlotShape_NotAggregateShape()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        var slot = json.SelectToken("targets[0].slots[0]");
        Assert.That(slot, Is.Not.Null);
        // Per-target TargetSlotTiming shape: {slot, durationMs, scriptsRun} ...
        Assert.That(slot.SelectToken("slot")?.Value<string>(), Is.EqualTo("ModifiedTables"));
        Assert.That(slot.SelectToken("durationMs")?.Value<long>(), Is.EqualTo(8021));
        Assert.That(slot.SelectToken("scriptsRun")?.Value<int>(), Is.EqualTo(0));
        // ... explicitly NOT the aggregate SlotTiming shape ({totalMs, targetCount}) — guards the
        // confusable TargetSlotTiming-vs-SlotTiming pair against a future rename.
        Assert.That(slot.SelectToken("totalMs"), Is.Null);
        Assert.That(slot.SelectToken("targetCount"), Is.Null);
    }

    [Test]
    public void Serialize_EmitsObjectChangeCountKeys_WithForeignKeysCamelCasing()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        foreach (var key in new[] { "tables", "columns", "indexes", "constraints", "foreignKeys", "procedures", "views", "functions" })
            Assert.That(json.SelectToken($"objectChanges.created.{key}"), Is.Not.Null, $"created.{key} missing");
        foreach (var key in new[] { "tables", "columns" })
            Assert.That(json.SelectToken($"objectChanges.modified.{key}"), Is.Not.Null, $"modified.{key} missing");
        foreach (var key in new[] { "tables", "indexes", "constraints", "foreignKeys" })
            Assert.That(json.SelectToken($"objectChanges.dropped.{key}"), Is.Not.Null, $"dropped.{key} missing");

        Assert.That(json.SelectToken("objectChanges.scriptsRan"), Is.Not.Null, "scriptsRan missing");

        // PascalCase ForeignKeys must NOT leak through the camelCase resolver.
        Assert.That(json.SelectToken("objectChanges.created.ForeignKeys"), Is.Null);
        Assert.That(json.SelectToken("objectChanges.dropped.ForeignKeys"), Is.Null);
    }

    [Test]
    public void Serialize_UnsupportedDowngrade_EmitsCamelCaseFeatureAndObjectName()
    {
        var summary = BuildFullyPopulatedSummary() with
        {
            UnsupportedDowngrade = new UnsupportedDowngradeSummary(new[]
            {
                new UnsupportedDowngradeEntry("NULLS NOT DISTINCT (PG15)", "public.t.uq_t")
            })
        };

        var json = JObject.Parse(DeploymentSummaryJson.Serialize(summary));

        Assert.That(json.SelectToken("unsupportedDowngrade.downgrades[0].feature")?.Value<string>(),
            Is.EqualTo("NULLS NOT DISTINCT (PG15)"));
        Assert.That(json.SelectToken("unsupportedDowngrade.downgrades[0].objectName")?.Value<string>(),
            Is.EqualTo("public.t.uq_t"));
    }

    [Test]
    public void Serialize_EmitsMigrationScriptFields()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary()));

        var script = json.SelectToken("migrationScripts[0]");
        Assert.That(script, Is.Not.Null);
        Assert.That(script.SelectToken("path")?.Value<string>(), Is.EqualTo("After Scripts/01_seed.sql"));
        Assert.That(script.SelectToken("slot")?.Value<string>(), Is.EqualTo("After"));
        Assert.That(script.SelectToken("template")?.Value<string>(), Is.EqualTo("TenantSchema"));
        Assert.That(script.SelectToken("schema")?.Value<string>(), Is.EqualTo("sales"));
        Assert.That(script.SelectToken("server")?.Value<string>(), Is.EqualTo("primary"));
        Assert.That(script.SelectToken("database")?.Value<string>(), Is.EqualTo("TenantA"));
        Assert.That(script.SelectToken("outcome")?.Value<string>(), Is.EqualTo("Ran"));
    }

    [Test]
    public void Serialize_EmitsUtcTimestampsAsIso8601WithZ()
    {
        var raw = DeploymentSummaryJson.Serialize(BuildFullyPopulatedSummary());

        // Read with DateParseHandling.None so JObject preserves the on-the-wire string form — the
        // default JObject.Parse re-interprets ISO dates into DateTime tokens and reformats them in
        // the local culture, hiding what a consumer's parser actually receives.
        using var reader = new JsonTextReader(new StringReader(raw)) { DateParseHandling = DateParseHandling.None };
        var json = JObject.Load(reader);

        Assert.That(json.SelectToken("run.startedUtc")?.Value<string>(), Is.EqualTo("2026-07-08T14:22:03Z"));
        Assert.That(json.SelectToken("run.finishedUtc")?.Value<string>(), Is.EqualTo("2026-07-08T14:23:31Z"));
    }

    [Test]
    public void Serialize_EmitsWhatIfAsJsonNull_WhenAbsent()
    {
        var summary = BuildFullyPopulatedSummary() with { WhatIf = null };

        var json = JObject.Parse(DeploymentSummaryJson.Serialize(summary));

        var whatIf = json.SelectToken("whatIf");
        Assert.That(whatIf, Is.Not.Null, "whatIf key must remain present in the contract");
        Assert.That(whatIf.Type, Is.EqualTo(JTokenType.Null));
    }
}
