// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// D13: the <c>Setup</c> slot runs after the baseline check and before the first object pass and the <c>Before</c> slot,
/// on every deploy, one pass, in order, stopping at the first error. Every engine; the SQL is portable.
/// </summary>
[NonParallelizable]
public abstract class SetupSlotSharedTests : ObjectScriptDeployFixture
{
    protected sealed override string ProductName => "SetupSlotProduct";

    private const string Folders = """
        { "FolderPath": "Setup", "QuenchSlot": "Setup" },
        { "FolderPath": "Early Objects", "QuenchSlot": "Objects" },
        { "FolderPath": "Before Scripts", "QuenchSlot": "Before" }
        """;

    /// <summary>Deletes this product's run-once records, so the Before slot's script runs again in the next test.</summary>
    protected abstract string ForgetRunOnceScriptsSql { get; }

    [SetUp]
    public void CreateLog()
    {
        Exec(ForgetRunOnceScriptsSql);
        Exec("DROP TABLE IF EXISTS setup_log");
        Exec("CREATE TABLE setup_log (step VARCHAR(40) NOT NULL)");
    }

    [OneTimeTearDown]
    public void DropLog()
    {
        Exec(ForgetRunOnceScriptsSql);
        Exec("DROP TABLE IF EXISTS setup_log");
    }

    private long Steps(string step) => Count($"SELECT COUNT(*) FROM setup_log WHERE step = '{step}'");

    [Test]
    public void ASetupScript_RunsOnEveryDeploy_AheadOfTheBeforeSlot()
    {
        WritePackage(Folders, "Setup", "Early Objects", "Before Scripts");
        WriteFile("Setup", "01-setting.sql", "INSERT INTO setup_log (step) VALUES ('setup');");
        // The first object pass records whether Setup had already run when it started.
        WriteFile("Early Objects", "01-object.sql", "INSERT INTO setup_log (step) SELECT 'object-after-setup' FROM setup_log WHERE step = 'setup';");
        // Run-once: on the first deploy it records whether Setup had already run.
        WriteFile("Before Scripts", "01-migration.sql", "INSERT INTO setup_log (step) SELECT 'before-after-setup' FROM setup_log WHERE step = 'setup';");

        var first = Deploy();
        var objectSawSetup = Steps("object-after-setup");
        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(objectSawSetup, Is.EqualTo(1), "the first object pass ran after Setup");
            Assert.That(Steps("setup"), Is.EqualTo(2), "a Setup script runs on every deploy: it is state, not a migration");
            Assert.That(Steps("before-after-setup"), Is.EqualTo(1), "the Before slot saw Setup's work, and stayed run-once");
            var setupLine = first.Log.FindIndex(l => l != null && l.Contains("Quenching Setup scripts"));
            var objectsLine = first.Log.FindIndex(l => l != null && l.Contains("Quenching object scripts without unresolved tokens"));
            Assert.That(setupLine, Is.GreaterThanOrEqualTo(0).And.LessThan(objectsLine), "Setup runs before the first object pass");
        });
    }

    // Setup changes the database, so it waits for the baseline check: a database the package refuses is left alone.
    [Test]
    public void ARefusedBaseline_StopsTheDeploy_BeforeAnySetupScriptRuns()
    {
        WritePackage(Folders, "Setup", "Early Objects", "Before Scripts");
        var templatePath = Path.Join(TemplateDir, "Template.json");
        File.WriteAllText(templatePath, File.ReadAllText(templatePath).Replace("\"ScriptFolders\"", "\"BaselineValidationScript\": \"SELECT 0\", \"ScriptFolders\""));
        WriteFile("Setup", "01-setting.sql", "INSERT INTO setup_log (step) VALUES ('setup');");

        var run = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(run.Log, Has.Some.Contains("Invalid baseline"), "the baseline refused the database: " + string.Join(" | ", run.Log));
            Assert.That(Steps("setup"), Is.Zero, "and no Setup script ran");
        });
    }

    // A resumed run skips the Setup scripts that succeeded before the failure and runs the rest.
    [Test]
    public void AResumedRun_SkipsTheSetupScriptsThatSucceeded_AndRunsTheRest()
    {
        Exec("DROP TABLE IF EXISTS setup_later");
        var checkpointDir = Path.Join(Path.GetTempPath(), $"SetupResume_{Guid.NewGuid():N}");
        try
        {
            WritePackage(Folders, "Setup", "Early Objects", "Before Scripts");
            WriteFile("Setup", "01-first.sql", "INSERT INTO setup_log (step) VALUES ('first');");
            WriteFile("Setup", "02-second.sql", "INSERT INTO setup_later (step) VALUES ('second');");

            Deploy(expectSuccess: false, checkpointDir: checkpointDir);
            Exec("CREATE TABLE setup_later (step VARCHAR(40) NOT NULL)");
            Deploy(checkpointDir: checkpointDir, resume: true);

            Assert.Multiple(() =>
            {
                Assert.That(Steps("first"), Is.EqualTo(1), "the script that succeeded before the failure is not run again");
                Assert.That(Count("SELECT COUNT(*) FROM setup_later WHERE step = 'second'"), Is.EqualTo(1), "the one that failed runs on the resume");
            });
        }
        finally
        {
            Exec("DROP TABLE IF EXISTS setup_later");
            if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
        }
    }

    [Test]
    public void UnderWhatIf_SetupScriptsAreListed_AndNotRun()
    {
        WritePackage(Folders, "Setup", "Early Objects", "Before Scripts");
        WriteFile("Setup", "01-setting.sql", "INSERT INTO setup_log (step) VALUES ('setup');");

        var preview = Deploy(whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(Steps("setup"), Is.Zero);
            Assert.That(preview.Log.Any(l => l != null && l.Contains("[WhatIf] Setup scripts")), Is.True, string.Join(" | ", preview.Log));
            Assert.That(preview.Log.Any(l => l != null && l.Contains("01-setting.sql")), Is.True);
        });
    }

    [Test]
    public void TheFirstFailingSetupScript_StopsTheDeploy_AndTheRestDoNotRun()
    {
        WritePackage(Folders, "Setup", "Early Objects", "Before Scripts");
        WriteFile("Setup", "01-broken.sql", "INSERT INTO no_such_table_for_setup (step) VALUES ('x');");
        WriteFile("Setup", "02-after.sql", "INSERT INTO setup_log (step) VALUES ('second');");
        WriteFile("Before Scripts", "01-migration.sql", "INSERT INTO setup_log (step) VALUES ('before');");

        var run = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(Steps("second"), Is.Zero, "nothing after the failing Setup script runs");
            Assert.That(Steps("before"), Is.Zero, "nor does any later slot");
            Assert.That(run.Log.Any(l => l != null && l.Contains("01-broken.sql") && l.StartsWith("ERROR")), Is.True, string.Join(" | ", run.Log));
            Assert.That(run.Log.Any(l => l != null && l.Contains("02-after.sql") && l.StartsWith("ERROR")), Is.False,
                "the script after the failure did not run, so it is not reported as failed");
        });
    }
}
