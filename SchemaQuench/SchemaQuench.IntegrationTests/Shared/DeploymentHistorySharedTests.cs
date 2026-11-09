// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// The deployment record on the target: a deploy writes one finished row, a failure says so, a run that dies leaves its
/// Started row, and read-only modes and DeploymentHistory off write nothing. Runs whole deploys through ProductQuench
/// against the suite's database; rows are keyed by this fixture's product and removed at both ends.
/// </summary>
public abstract class DeploymentHistorySharedTests : ObjectScriptDeployFixture
{
    protected abstract string ViewScript(string view, int value);
    protected abstract string DropView(string view);

    private const string View = "history_v";
    private const string OtherProduct = "HistoryOtherProduct";

    private string Table => DeploymentHistory.Table(Platform);
    private string C(string column) => DeploymentHistory.Column(Platform, column);

    [SetUp]
    public void APackageWithAView()
    {
        ClearRows();
        Exec(DropView(View));
        WritePackage("""{ "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" }""", "Views");
        WriteFile("Views", $"{View}.sql", ViewScript(View, 1));
    }

    [TearDown]
    public void RemoveIt()
    {
        Exec(DropView(View));
        ClearRows();
    }

    private void ClearRows() =>
        Exec($"DELETE FROM {Table} WHERE {C("Product")} IN ('{ProductName}', '{OtherProduct}')");

    private long Rows(Guid runId) => Count($"SELECT COUNT(*) FROM {Table} WHERE {C("RunId")} = '{runId}'");

    private object Field(Guid runId, string column) => Scalar($"SELECT {C(column)} FROM {Table} WHERE {C("RunId")} = '{runId}'");

    private Guid NewRun()
    {
        RunContext.Start("SchemaQuench");
        return RunContext.Id;
    }

    [Test]
    public void ADeploy_WritesOneFinishedSuccessRow_NamingTheRunAndTheLoginTheEngineSaw()
    {
        var runId = NewRun();
        var run = RunDrift(switches: "");
        Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

        var login = Scalar(SessionLogin.Query(Platform))?.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(Rows(runId), Is.EqualTo(1), "one row for the one target");
            Assert.That(Field(runId, "Outcome"), Is.EqualTo("Success"));
            Assert.That(Field(runId, "FinishedUtc"), Is.Not.Null.And.Not.EqualTo(DBNull.Value), "finished");
            Assert.That(Field(runId, "Product"), Is.EqualTo(ProductName));
            Assert.That(Field(runId, "Template"), Is.EqualTo("Main"));
            Assert.That(Field(runId, "SchemaName"), Is.EqualTo(DBNull.Value), "a regular template has no schema");
            Assert.That(Field(runId, "ToolVersion")?.ToString(), Is.Not.Empty);
            Assert.That(Field(runId, "DeployedAs"), Is.EqualTo(login), "the engine's own answer, not the settings file");
            Assert.That(Convert.ToInt32(Field(runId, "ChangesApplied")), Is.GreaterThanOrEqualTo(1), "the view script ran");
            var started = Convert.ToDateTime(Field(runId, "StartedUtc"));
            Assert.That(started, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(10)), "UTC, not the server's local time");
        });
    }

    [Test]
    public void AFailingScript_LeavesAFinishedFailedRow()
    {
        WriteFile("Views", $"{View}.sql", "CREATE VIEW history_broken AS SELEC 1");
        var runId = NewRun();
        var run = RunDrift(switches: "");
        Assert.That(run.ExitCode, Is.EqualTo(2), run.Describe());

        Assert.Multiple(() =>
        {
            Assert.That(Rows(runId), Is.EqualTo(1));
            Assert.That(Field(runId, "Outcome"), Is.EqualTo("Failed"));
            Assert.That(Field(runId, "FinishedUtc"), Is.Not.EqualTo(DBNull.Value));
        });
    }

    [Test]
    public void Off_WritesNothing()
    {
        var runId = NewRun();
        var run = RunDrift(switches: "", settings: new Dictionary<string, string> { ["DeploymentHistory"] = "off" });
        Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

        Assert.That(Rows(runId), Is.EqualTo(0));
    }

    [Test]
    public void AWhatIf_WritesNothing()
    {
        var runId = NewRun();
        var run = RunDrift(switches: "--WhatIf");
        Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

        Assert.That(Rows(runId), Is.EqualTo(0));
    }

    // Tier 0 on every engine (the helpers the deploy kindled answer), so MySQL and MariaDB need no helper database.
    [Test]
    public void ADriftCheck_WritesNothing()
    {
        Assert.That(RunDrift(switches: "").ExitCode, Is.EqualTo(0));
        var runId = NewRun();
        var run = RunDrift(settings: new Dictionary<string, string> { ["KindleTheForge"] = "false" });
        Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

        Assert.That(Rows(runId), Is.EqualTo(0));
    }

    // Written on its own connection and committed at once, so a run that dies before the finish leaves this row.
    [Test]
    public void TheStartedRow_IsCommittedByItself()
    {
        var runId = Guid.NewGuid();
        using (var own = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString))
        {
            own.Open();
            if (Platform.GetBasePlatform() != Schema.Domain.Platform.MySQL) own.ChangeDatabase(MainDb);
            using var command = own.CreateCommand();
            DeploymentHistory.Start(command, Platform, Record(runId, ProductName), "Main", null, "someone", DateTime.UtcNow);
        }

        Assert.That(Field(runId, "Outcome"), Is.EqualTo("Started"));
        Assert.That(Field(runId, "FinishedUtc"), Is.EqualTo(DBNull.Value));
    }

    [Test]
    public void Retention_RemovesThisProductsOldRows_AndLeavesAnotherProductsAndRecentOnes()
    {
        var old = OldRow(ProductName);
        var otherOld = OldRow(OtherProduct);
        var runId = NewRun();
        Assert.That(RunDrift(switches: "").ExitCode, Is.EqualTo(0));

        Assert.Multiple(() =>
        {
            Assert.That(Rows(old), Is.EqualTo(0), "this product's row older than 400 days");
            Assert.That(Rows(otherOld), Is.EqualTo(1), "another product's rows are its own deploys' to prune");
            Assert.That(Rows(runId), Is.EqualTo(1), "the run's own row");
        });
    }

    [Test]
    public void RetentionZero_KeepsEverything()
    {
        var old = OldRow(ProductName);
        NewRun();
        Assert.That(RunDrift(switches: "", settings: new Dictionary<string, string> { ["DeploymentHistoryRetentionDays"] = "0" }).ExitCode, Is.EqualTo(0));

        Assert.That(Rows(old), Is.EqualTo(1));
    }

    // A helper table, so the package never declares it and dropping what the package no longer declares leaves it.
    [Test]
    public void DroppingTablesRemovedFromTheProduct_LeavesTheRecord()
    {
        NewRun();
        Assert.That(RunDrift(switches: "").ExitCode, Is.EqualTo(0));
        var second = NewRun();
        var run = RunDrift(switches: "", settings: new Dictionary<string, string> { ["DropTablesRemovedFromProduct"] = "true" });
        Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

        Assert.That(Count($"SELECT COUNT(*) FROM {Table} WHERE {C("Product")} = '{ProductName}'"), Is.EqualTo(2), "both runs' rows");
        Assert.That(Rows(second), Is.EqualTo(1));
    }

    private Guid OldRow(string product)
    {
        var runId = Guid.NewGuid();
        using var command = Connection.CreateCommand();
        DeploymentHistory.Start(command, Platform, Record(runId, product), "Main", null, "someone", DateTime.UtcNow.AddDays(-500));
        return runId;
    }

    private static DeploymentRecordRun Record(Guid runId, string product) =>
        new(runId, product, "test", null, null, null, null, null, 400, null, null);
}
