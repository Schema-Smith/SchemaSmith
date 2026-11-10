// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>The comment that opens this engine's block of example queries on the reference page.</summary>
    protected abstract string ReferenceQueriesMarker { get; }
    /// <summary>A table using a feature this server lacks, so the deploy downgrades it; null when the server takes it.</summary>
    protected abstract (string File, string Json, string Drop)? DowngradedTable(string table);

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

    private static string FullLog(DriftRun run) => run.Describe() + " || full log: " + string.Join(" | ", run.Log);

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
        Assert.That(run.ExitCode, Is.EqualTo(2), FullLog(run));

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
        var deploy = RunDrift(switches: "");
        Assert.That(deploy.ExitCode, Is.EqualTo(0), FullLog(deploy));
        var runId = NewRun();
        var run = RunDrift(settings: new Dictionary<string, string> { ["KindleTheForge"] = "false" });
        Assert.That(run.ExitCode, Is.EqualTo(0), "the drift check: " + FullLog(run) + " ||| the deploy before it: " + FullLog(deploy));

        Assert.That(Rows(runId), Is.EqualTo(0));
    }

    // Committed at once, so a run that dies before the finish leaves this row.
    [Test]
    public void TheStartedRow_IsCommittedByItself()
    {
        var runId = Guid.NewGuid();
        using (var own = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString))
        {
            own.Open();
            if (Platform.GetBasePlatform() != Schema.Domain.Platform.MySQL) own.ChangeDatabase(MainDb);
            using var command = own.CreateCommand();
            DeploymentHistory.Start(command, Platform, Record(runId, ProductName), "Main", null, "someone", null, DateTime.UtcNow);
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

    // The reference page's example queries for this engine run against a database that has the record.
    [Test]
    public void TheReferencePagesQueries_Run()
    {
        NewRun();
        Assert.That(RunDrift(switches: "").ExitCode, Is.EqualTo(0));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln"))) dir = dir.Parent;
        var page = File.ReadAllText(Path.Join(dir!.FullName, "docs", "end-user", "reference", "schemaquench.md")).Replace("\r\n", "\n");
        var section = page[page.IndexOf("## Deployment Record", StringComparison.Ordinal)..];
        var start = section.IndexOf(ReferenceQueriesMarker + "\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(0), $"the page's '{ReferenceQueriesMarker}' block");
        var block = section[start..section.IndexOf("```", start, StringComparison.Ordinal)];
        var statements = block.Split(';').Select(q => string.Join("\n", q.Split('\n').Where(l => !l.TrimStart().StartsWith("--")))).Where(q => q.Trim().Length > 0).ToList();
        Assert.That(statements, Has.Count.EqualTo(3), "the last deployments, the last 30 days, and runs never finished");

        foreach (var statement in statements)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = statement;
            using var reader = command.ExecuteReader();
            Assert.That(reader.FieldCount, Is.GreaterThan(0), statement);
        }
        Assert.That(Scalar(statements[0].Replace("SELECT TOP (5) StartedUtc", "SELECT TOP (5) Outcome").Replace("SELECT \"StartedUtc\"", "SELECT \"Outcome\"").Replace("SELECT StartedUtc", "SELECT Outcome")), Is.EqualTo("Success"),
            "the first query finds the latest deployment");
    }

    // A record that cannot be written is one warning, never a failed deploy and never an error line. The table is dropped and
    // the helpers left as they are (KindleTheForge false), as on a database kindled before the record existed.
    [Test]
    public void AFailedRecordWrite_IsOneWarning_AndTheDeploySucceeds()
    {
        Assert.That(RunDrift(switches: "").ExitCode, Is.EqualTo(0));
        Exec($"DROP TABLE {Table}");
        try
        {
            NewRun();
            var run = RunDrift(switches: "", settings: new Dictionary<string, string> { ["KindleTheForge"] = "false" });

            Assert.Multiple(() =>
            {
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
                Assert.That(run.Log.Where(l => l != null && l.StartsWith("ERROR")), Is.Empty, "a record write is no deploy error");
                Assert.That(run.Log.Count(l => l != null && l.StartsWith("WARN") && l.Contains("deployment record could not be started")), Is.EqualTo(1));
            });
        }
        finally
        {
            Assert.That(RunDrift(switches: "", settings: new Dictionary<string, string> { ["ForceReKindle"] = "true" }).ExitCode, Is.EqualTo(0), "put the table back");
        }
    }

    // The finish fails too without failing the deploy: a script drops the table after the Started row is in, so only the
    // finish's write meets the missing table.
    [Test]
    public void AFailedFinish_IsOneWarning_AndTheDeploySucceeds()
    {
        WriteFile("Views", "zz_drop_record.sql", $"DROP TABLE {Table}");
        try
        {
            NewRun();
            var run = RunDrift(switches: "");

            Assert.Multiple(() =>
            {
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
                Assert.That(run.Log.Where(l => l != null && l.StartsWith("ERROR")), Is.Empty, "a record write is no deploy error");
                Assert.That(run.Log.Count(l => l != null && l.StartsWith("WARN") && l.Contains("deployment record could not be finished")), Is.EqualTo(1));
            });
        }
        finally
        {
            File.Delete(Path.Join(TemplateDir, "Views", "zz_drop_record.sql"));
            Assert.That(RunDrift(switches: "", settings: new Dictionary<string, string> { ["ForceReKindle"] = "true" }).ExitCode, Is.EqualTo(0), "put the table back");
        }
    }

    // Each column named for what fills it, so a value written into its neighbour's column is caught.
    [Test]
    public void TheRunsProvenance_FillsItsColumns()
    {
        var runId = NewRun();
        var run = RunDrift(switches: "", settings: new Dictionary<string, string>
        {
            ["RunLabel"] = "label-2026.11.3", ["RunCommit"] = "commit-4f2a9c1e", ["RunBranch"] = "branch-release",
            ["RunUrl"] = "https://ci.example/runs/42", ["RunActor"] = "actor-octocat"
        });
        Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

        Assert.Multiple(() =>
        {
            Assert.That(Field(runId, "RunLabel"), Is.EqualTo("label-2026.11.3"));
            Assert.That(Field(runId, "SourceCommit"), Is.EqualTo("commit-4f2a9c1e"));
            Assert.That(Field(runId, "SourceBranch"), Is.EqualTo("branch-release"));
            Assert.That(Field(runId, "PipelineUrl"), Is.EqualTo("https://ci.example/runs/42"));
            Assert.That(Field(runId, "Actor"), Is.EqualTo("actor-octocat"));
            Assert.That(Field(runId, "PackageHash")?.ToString(), Does.Match("^sha256:[0-9a-f]{64}$"), "filled when the record finishes");
            Assert.That(Field(runId, "ResumedFromRunId"), Is.EqualTo(DBNull.Value), "not a resume");
        });
    }

    // A schema template deploys each schema as its own unit, and each unit writes its own row, named for its schema.
    [Test]
    public void ASchemaTemplate_WritesOneRowPerSchema()
    {
        if (Platform.GetBasePlatform() == Schema.Domain.Platform.MySQL) Assert.Ignore("Schema templates are SQL Server and PostgreSQL only.");
        string[] schemas = ["hist_a", "hist_b"];
        foreach (var schema in schemas)
            Exec(Platform == Schema.Domain.Platform.SqlServer
                ? $"IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA {schema}')"
                : $"CREATE SCHEMA IF NOT EXISTS {schema}");
        WritePackage("");
        File.WriteAllText(Path.Join(TemplateDir, "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT '{{MainDb}}'",
              "SchemaIdentificationScript": "SELECT 'hist_a' UNION ALL SELECT 'hist_b'", "ScriptFolders": [] }
            """);
        try
        {
            var runId = NewRun();
            var run = RunDrift(switches: "");
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());

            var named = new List<string>();
            using (var command = Connection.CreateCommand())
            {
                command.CommandText = $"SELECT {C("SchemaName")} FROM {Table} WHERE {C("RunId")} = '{runId}' ORDER BY {C("SchemaName")}";
                using var reader = command.ExecuteReader();
                while (reader.Read()) named.Add(reader.GetString(0));
            }
            Assert.That(named, Is.EqualTo(schemas));
        }
        finally
        {
            foreach (var schema in schemas)
                Exec(Platform == Schema.Domain.Platform.SqlServer ? $"DROP SCHEMA IF EXISTS {schema}" : $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    // A feature the target cannot take is downgraded, and the target's row says Incomplete. Each engine names a feature its
    // older servers lack, so some leg of the matrix runs this on every engine.
    [Test]
    public void ADowngradeOnTheTarget_FinishesTheRowIncomplete()
    {
        const string table = "history_down";
        if (DowngradedTable(table) is not { } down)
        {
            Assert.Ignore("This server takes the feature, so nothing is downgraded.");
            return;
        }
        Exec(down.Drop);
        WritePackage("");
        WriteFile("Tables", down.File, down.Json);
        try
        {
            var runId = NewRun();
            var run = RunDrift(switches: "");
            Assert.That(run.ExitCode, Is.EqualTo(1), run.Describe());

            Assert.That(Field(runId, "Outcome"), Is.EqualTo("Incomplete"));

            // #616: a pipeline that expects the downgrade exits 0, and the record still says Incomplete.
            Exec(down.Drop);
            var acceptingId = NewRun();
            var accepting = RunDrift(switches: "--ExitZeroOnDowngrade");
            Assert.That(accepting.ExitCode, Is.EqualTo(0), accepting.Describe());
            Assert.That(Field(acceptingId, "Outcome"), Is.EqualTo("Incomplete"));
        }
        finally
        {
            Exec(down.Drop);
        }
    }

    private Guid OldRow(string product)
    {
        var runId = Guid.NewGuid();
        using var command = Connection.CreateCommand();
        DeploymentHistory.Start(command, Platform, Record(runId, product), "Main", null, "someone", null, DateTime.UtcNow.AddDays(-500));
        return runId;
    }

    private static DeploymentRecordRun Record(Guid runId, string product) =>
        new(runId, product, "test", null, null, null, null, null, 400, null, null);
}
