// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using NUnit.Framework;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Holds the published JSON Schema for Summary.json to the model it is generated from and to the summaries the writer
/// produces: the committed file is regenerated and compared, and summaries of every shape validate against it.
/// </summary>
[TestFixture]
public class DeploymentSummarySchemaTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    internal static string CommittedPath() =>
        Path.Join(RepoRoot(), "docs", "end-user", "reference", "schemas", DeploymentSummarySchema.FileName);

    // The shared builder pins an older version string for the JSON tests; these validate a current summary.
    private static DeploymentSummary Full() =>
        DeploymentSummaryJsonTests.BuildFullyPopulatedSummary() with { SchemaVersion = DeploymentSummaryAssembler.SchemaVersion };

    private static JsonSchema Schema() => JsonSchema.FromJsonAsync(DeploymentSummarySchema.Render()).GetAwaiter().GetResult();

    private static string[] Errors(DeploymentSummary summary) =>
        Schema().Validate(DeploymentSummaryJson.Serialize(summary)).Select(e => $"{e.Path}: {e.Kind}").ToArray();

    [Test]
    public void TheCommittedSchemaIsCurrent()
    {
        Assert.That(RepoRoot(), Is.Not.Null, "could not locate the repository root");
        var path = CommittedPath();
        var expected = DeploymentSummarySchema.Render();
        var actual = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
        if (actual == expected) return;

        // A released file never changes: a model change is a new schema version, and a new file.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, expected);
        Assert.Fail($"{DeploymentSummarySchema.FileName} was {(actual == null ? "missing" : "out of date with the summary model")} and has been "
                    + "regenerated. If this version was already released, bump DeploymentSummaryAssembler.SchemaVersion instead.");
    }

    [Test]
    public void AFullyPopulatedSummary_Validates()
    {
        var summary = Full();
        summary = summary with
        {
            Run = summary.Run with
            {
                Settings = new RunSettings(0, 10, 20), DriftTier = 1, Id = Guid.NewGuid(), Label = "2026.11.3", ResumesRunId = Guid.NewGuid(),
                Package = new PackageIdentity("sha256:00", 3), Source = new RunSource("acme/db", "abc", "main", "ci"),
                Ci = new CiInfo("GitHubActions", "Deploy", "1", "1", "https://x", "octocat", "production"), Host = new RunHost("BUILD01", "svc")
            },
            Targets = summary.Targets.Select(t => t with { ConnectedAs = "deploy_user" }).ToList(),
            PreventDrop = new PreventDropSummary(true, [new WouldDropEntry("Table", "[dbo].[Old]")]),
            UnsupportedDowngrade = new UnsupportedDowngradeSummary([new UnsupportedDowngradeEntry("NULLS NOT DISTINCT (PG15)", "ix")]),
            LockWaits = [new LockWait("[s].[d]", "ModifiedTables", 2, 1500, "succeeded")],
            Destructive = new DestructiveSummary("warn", [new DestructiveChange("dropColumn", "[dbo].[T].[C]", "detail", "s", "d", 10, "catalog", false, "applied")]),
            SettingOutcomes = new SettingsSummary(new SettingsScopeCounts(1, 0, 0, 0, 0, 0), null)
        };

        Assert.That(Errors(summary), Is.Empty);
    }

    [Test]
    public void AFailedRun_ADriftCheck_AndAMinimalSummary_Validate()
    {
        var full = Full();
        var drift = full with
        {
            Run = full.Run with { Mode = RunMode.Drift, DriftTier = 0 },
            Drift = new DriftReport(true, ["create"],
                [new DriftTargetReport("s", "d", null, "Main", "drifted", 1, "plan", [new DriftItem("s", "d", null, "Table", "[dbo].[T]", "create")])],
                [new DriftPlanGroup("Main", "plan", 1, "[s].[d]")], [], [], [])
        };
        var minimal = full with
        {
            Run = full.Run with { Outcome = RunOutcome.Aborted, ExitCode = 3 },
            Targets = [], MigrationScripts = [], Failures = [], WhatIf = null, LockWaits = null,
            Timing = new TimingSummary(0, [], [], []),
            ObjectChanges = new ObjectChangeSummary(false, new CreatedCounts(0, 0, 0, 0, 0, 0, 0, 0), new ModifiedCounts(0, 0),
                new DroppedCounts(0, 0, 0, 0, 0), 0, [])
        };

        Assert.That(Errors(drift), Is.Empty, "drift");
        Assert.That(Errors(minimal), Is.Empty, "minimal");
    }

    [Test]
    public void AFieldTheSchemaLacks_FailsValidation()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(Full()));
        ((JObject)json["run"]!)["unexpected"] = "x";

        Assert.That(Schema().Validate(json.ToString()), Is.Not.Empty);
    }

    // Written every time, even when its value is the parameter's default, so a consumer can rely on it.
    [Test]
    public void APropertyWithADefaultedParameter_IsRequired()
    {
        var schema = DeploymentSummarySchema.Generate();
        var objectChanges = (JArray)schema.SelectToken("definitions.ObjectChangeSummary.required")!;
        var target = (JArray)schema.SelectToken("definitions.TargetSummary.required")!;

        Assert.That(objectChanges.Values<string>(), Does.Contain("scriptsUnchanged"));
        Assert.That(target.Values<string>(), Does.Contain("databaseSource").And.Contain("connectedAs"));
    }

    // Left out of the JSON when null, so a consumer cannot require it.
    [Test]
    public void APropertyIgnoredWhenNull_IsOptional()
    {
        var required = (JArray)DeploymentSummarySchema.Generate().SelectToken("definitions.ObjectChangeDetail.required")!;

        Assert.That(required.Values<string>(), Does.Contain("objectName").And.Not.Contain("mode").And.Not.Contain("server"));
    }

    [Test]
    public void TheSchemaVersionIsPinned_AndTheFileNamedForIt()
    {
        var schema = DeploymentSummarySchema.Generate();

        Assert.That(schema.SelectToken("properties.schemaVersion.enum")!.Values<string>(), Is.EqualTo(new[] { "1.4" }));
        Assert.That(DeploymentSummarySchema.FileName, Is.EqualTo("deployment-summary-1.4.schema.json"));
    }

    [Test]
    public void AnotherSchemaVersion_FailsValidation()
    {
        var summary = Full() with { SchemaVersion = "1.3" };

        Assert.That(Errors(summary), Is.Not.Empty);
    }
}
