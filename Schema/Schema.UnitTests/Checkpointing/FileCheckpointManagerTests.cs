// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Checkpointing;

namespace Schema.UnitTests.Checkpointing;

[TestFixture]
public class FileCheckpointManagerTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"checkpoint_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private TrackingScope DbScope(string product = "TestProduct", string template = "Main",
        string server = "localhost", string database = "TestDB") =>
        new() { ProductName = product, TemplateName = template, Server = server, DatabaseName = database };

    private TrackingScope ProductScope(string product = "TestProduct") =>
        new() { ProductName = product };

    [Test]
    public void Track_DatabaseScope_RunsActionOnFirstCall()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var ran = false;

        mgr.Track(DbScope(), "KindleForge", () => ran = true);

        Assert.That(ran, Is.True);
    }

    [Test]
    public void Track_DatabaseScope_SkipsActionOnSecondCall()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var count = 0;

        mgr.Track(DbScope(), "KindleForge", () => count++);
        mgr.Track(DbScope(), "KindleForge", () => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Track_DatabaseScope_DifferentSteps_BothRun()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var count = 0;

        mgr.Track(DbScope(), "KindleForge", () => count++);
        mgr.Track(DbScope(), "ValidateBaseline", () => count++);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void Track_DatabaseScope_PersistsToDisk()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(DbScope(), "KindleForge", () => { });

        var mgr2 = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ran = false;
        mgr2.Track(DbScope(), "KindleForge", () => ran = true);

        Assert.That(ran, Is.False);
    }

    [Test]
    public void TrackScript_DatabaseScope_RunsOnFirstCall()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var ran = false;

        mgr.TrackScript(DbScope(), "Before", "scripts/init.sql", () => ran = true);

        Assert.That(ran, Is.True);
    }

    [Test]
    public void TrackScript_DatabaseScope_SkipsOnSecondCall()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var count = 0;

        mgr.TrackScript(DbScope(), "Before", "scripts/init.sql", () => count++);
        mgr.TrackScript(DbScope(), "Before", "scripts/init.sql", () => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void TrackScript_DatabaseScope_DifferentSlots_BothRun()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var count = 0;

        mgr.TrackScript(DbScope(), "Before", "scripts/init.sql", () => count++);
        mgr.TrackScript(DbScope(), "After", "scripts/init.sql", () => count++);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void TrackScript_DatabaseScope_PersistsToDisk()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        mgr.TrackScript(DbScope(), "Object", "views/MyView.sql", () => { });

        var mgr2 = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ran = false;
        mgr2.TrackScript(DbScope(), "Object", "views/MyView.sql", () => ran = true);

        Assert.That(ran, Is.False);
    }

    [Test]
    public void Track_ProductScope_RunsAndSkips()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var count = 0;

        mgr.Track(ProductScope(), "MainTemplate", () => count++);
        mgr.Track(ProductScope(), "MainTemplate", () => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void TrackScript_ProductScope_Before_RunsAndSkips()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var scope = new TrackingScope { ProductName = "TestProduct", Server = "server1" };
        var count = 0;

        mgr.TrackScript(scope, "Before", "scripts/before1.sql", () => count++);
        mgr.TrackScript(scope, "Before", "scripts/before1.sql", () => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void TrackScript_ProductScope_After_RunsAndSkips()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var scope = new TrackingScope { ProductName = "TestProduct", Server = "server1" };
        var count = 0;

        mgr.TrackScript(scope, "After", "scripts/after1.sql", () => count++);
        mgr.TrackScript(scope, "After", "scripts/after1.sql", () => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void DeleteCheckpoints_RemovesAllFilesForProduct()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(DbScope(), "KindleForge", () => { });
        mgr.Track(ProductScope(), "MainTemplate", () => { });

        mgr.DeleteCheckpoints("TestProduct");

        var mgr2 = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var count = 0;
        mgr2.Track(DbScope(), "KindleForge", () => count++);
        mgr2.Track(ProductScope(), "MainTemplate", () => count++);

        Assert.That(count, Is.EqualTo(2));
    }

    // A plain run deleted every checkpoint of the product, so a failed deploy to one database lost its resume point to a
    // plain deploy of the same product to another.
    [Test]
    public void DeleteCheckpointsOfThisRun_KeepsAnotherTargetsResumePoint()
    {
        new FileCheckpointManager(_tempDir, adoptExisting: false).Track(DbScope(database: "TenantA"), "ModifiedTables", () => { });
        var plain = new FileCheckpointManager(_tempDir, adoptExisting: false);
        plain.Track(DbScope(database: "TenantB"), "ModifiedTables", () => { });
        plain.DeleteCheckpointsOfThisRun("TestProduct");

        var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ranA = false;
        var ranB = false;
        resume.Track(DbScope(database: "TenantA"), "ModifiedTables", () => ranA = true);
        resume.Track(DbScope(database: "TenantB"), "ModifiedTables", () => ranB = true);

        Assert.Multiple(() =>
        {
            Assert.That(ranA, Is.False, "the failed target resumes");
            Assert.That(ranB, Is.True, "the plain run's own target starts fresh");
            Assert.That(resume.ResumedAny, Is.True);
        });
    }

    [Test]
    public void APlainRun_DiscardsAStaleCheckpointOfItsOwnTarget_WhenItReachesIt()
    {
        new FileCheckpointManager(_tempDir, adoptExisting: false).Track(DbScope(), "ModifiedTables", () => { });
        var plain = new FileCheckpointManager(_tempDir, adoptExisting: false);
        Assert.That(plain.HasCompleted(DbScope(), "ModifiedTables"), Is.False);

        var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ran = false;
        resume.Track(DbScope(), "ModifiedTables", () => ran = true);

        Assert.Multiple(() =>
        {
            Assert.That(ran, Is.True, "a resume after the plain run does not take up the older run's steps");
            Assert.That(resume.ResumedAny, Is.False);
        });
    }

    [Test]
    public void DatabaseCheckpoint_ParseFormat_RoundTrip()
    {
        var checkpoint = new DatabaseCheckpoint
        {
            ProductName = "MyProduct",
            TemplateName = "Main",
            Server = "localhost",
            DatabaseName = "TestDB",
            StartedAt = new DateTime(2026, 4, 11, 10, 30, 0)
        };
        checkpoint.AddStep("KindleForge");
        checkpoint.AddStep("ValidateBaseline");
        checkpoint.AddCompletedScript(DatabaseScriptSlot.Setup, "setup/enable-cdc.sql");
        checkpoint.AddCompletedScript(DatabaseScriptSlot.Before, "scripts/init.sql");
        checkpoint.AddCompletedScript(DatabaseScriptSlot.Object, "views/MyView.sql");

        var formatted = FileCheckpointManager.FormatDatabaseCheckpoint(checkpoint);
        var parsed = FileCheckpointManager.ParseDatabaseCheckpoint(formatted, "MyProduct", "Main", "localhost", "TestDB");

        Assert.That(parsed.HasCompletedStep("KindleForge"), Is.True);
        Assert.That(parsed.HasCompletedStep("ValidateBaseline"), Is.True);
        Assert.That(parsed.HasCompletedScript(DatabaseScriptSlot.Setup, "setup/enable-cdc.sql"), Is.True, "D13: a resumed run skips the Setup scripts that succeeded");
        Assert.That(parsed.HasCompletedScript(DatabaseScriptSlot.Before, "scripts/init.sql"), Is.True);
        Assert.That(parsed.HasCompletedScript(DatabaseScriptSlot.Object, "views/MyView.sql"), Is.True);
        Assert.That(parsed.StartedAt, Is.EqualTo(new DateTime(2026, 4, 11, 10, 30, 0)));
    }

    // A checkpoint's start time is UTC, as the deployment summary's times are, and reads back as UTC.
    [Test]
    public void ANewCheckpoint_RecordsItsStartInUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var checkpoint = new DatabaseCheckpoint { ProductName = "P", TemplateName = "Main", Server = "s", DatabaseName = "d" };

        var formatted = FileCheckpointManager.FormatDatabaseCheckpoint(checkpoint);
        var parsed = FileCheckpointManager.ParseDatabaseCheckpoint(formatted, "P", "Main", "s", "d");

        Assert.Multiple(() =>
        {
            Assert.That(formatted, Does.Match(@"# Started: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z"));
            Assert.That(parsed.StartedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(parsed.StartedAt, Is.InRange(before, DateTime.UtcNow.AddSeconds(1)));
        });
    }

    [Test]
    public void ProductCheckpoint_ParseFormat_RoundTrip()
    {
        var checkpoint = new ProductCheckpoint
        {
            ProductName = "MyProduct",
            StartedAt = new DateTime(2026, 4, 11, 10, 30, 0)
        };
        checkpoint.AddBeforeScript("server1", "scripts/before1.sql");
        checkpoint.AddTemplate("Main");
        checkpoint.AddAfterScript("server1", "scripts/after1.sql");

        var formatted = FileCheckpointManager.FormatProductCheckpoint(checkpoint);
        var parsed = FileCheckpointManager.ParseProductCheckpoint(formatted, "MyProduct");

        Assert.That(parsed.HasCompletedBeforeScript("server1", "scripts/before1.sql"), Is.True);
        Assert.That(parsed.HasCompletedTemplate("Main"), Is.True);
        Assert.That(parsed.HasCompletedAfterScript("server1", "scripts/after1.sql"), Is.True);
        Assert.That(parsed.StartedAt, Is.EqualTo(new DateTime(2026, 4, 11, 10, 30, 0)));
    }

    [Test]
    public void TrackScript_NormalizesBackslashes()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var count = 0;

        mgr.TrackScript(DbScope(), "Before", @"scripts\init.sql", () => count++);
        mgr.TrackScript(DbScope(), "Before", "scripts/init.sql", () => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Track_DatabaseScope_CorruptedCheckpointFile_ReturnsFreshCheckpoint()
    {
        var scope = DbScope();

        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(scope, "KindleForge", () => { });

        var checkpointFile = Directory.GetFiles(_tempDir, "*.checkpoint")
            .Single(f => !f.EndsWith(".product.checkpoint"));
        File.WriteAllText(checkpointFile, "THIS IS NOT VALID CHECKPOINT CONTENT @@##!!");

        var mgr2 = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ran = false;
        mgr2.Track(scope, "KindleForge", () => ran = true);

        Assert.That(ran, Is.True, "Corrupted checkpoint should be silently discarded, causing the step to run again");
    }

    [Test]
    public void Track_ProductScope_CorruptedProductCheckpoint_ReturnsFreshCheckpoint()
    {
        var scope = ProductScope();

        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(scope, "MainTemplate", () => { });

        var productCheckpointFile = Directory.GetFiles(_tempDir, "*.product.checkpoint").Single();
        File.WriteAllText(productCheckpointFile, "\x00\x01\x02 BINARY GARBAGE \xFF\xFE");

        var mgr2 = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ran = false;
        mgr2.Track(scope, "MainTemplate", () => ran = true);

        Assert.That(ran, Is.True, "Corrupted product checkpoint should be silently discarded, causing the step to run again");
    }

    [Test]
    public void Complete_WhenNoExistingCheckpoints_DoesNotThrow()
    {
        var mgr = new FileCheckpointManager(_tempDir);

        Assert.DoesNotThrow(() => mgr.DeleteCheckpoints("TestProduct"));
    }

    [Test]
    public void Track_DatabaseScope_AfterComplete_RunsAgain()
    {
        var dbScope = DbScope();
        var productScope = ProductScope();

        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(dbScope, "KindleForge", () => { });
        mgr.Track(productScope, "MainTemplate", () => { });
        mgr.DeleteCheckpoints("TestProduct");

        var mgr2 = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var count = 0;
        mgr2.Track(dbScope, "KindleForge", () => count++);
        mgr2.Track(productScope, "MainTemplate", () => count++);

        Assert.That(count, Is.EqualTo(2), "Both steps should run after checkpoints were cleaned up by DeleteCheckpoints");
    }

    [Test]
    public void Constructor_CreatesDirectoryIfMissing()
    {
        var newDir = Path.Combine(_tempDir, "subdir");
        Assert.That(Directory.Exists(newDir), Is.False);

        _ = new FileCheckpointManager(newDir);

        Assert.That(Directory.Exists(newDir), Is.True);
    }

    [Test]
    public void Constructor_ThrowsOnNullDirectory()
    {
        Assert.Throws<ArgumentNullException>(() => new FileCheckpointManager(null));
    }

    [Test]
    public void DefaultConstructor_UsesTempDirectory()
    {
        var mgr = new FileCheckpointManager();
        var ran = false;

        mgr.Track(new TrackingScope { ProductName = $"TestProduct_{Guid.NewGuid():N}" }, "Step1", () => ran = true);

        Assert.That(ran, Is.True);
    }

    [Test]
    public void GetFromFactory_ReturnsICheckpointingInstance()
    {
        var checkpointing = FileCheckpointManager.GetFromFactory();

        Assert.That(checkpointing, Is.Not.Null);
        Assert.That(checkpointing, Is.InstanceOf<ICheckpointing>());
    }

    [Test]
    public void HasCompleted_DatabaseScope_MatchesTrack()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        Assert.That(mgr.HasCompleted(DbScope(), "KindleForge"), Is.False);

        mgr.Track(DbScope(), "KindleForge", () => { });

        Assert.That(mgr.HasCompleted(DbScope(), "KindleForge"), Is.True);
        Assert.That(mgr.HasCompleted(DbScope(), "ValidateBaseline"), Is.False);
    }

    [Test]
    public void HasCompleted_ProductScope_MatchesTrack()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        Assert.That(mgr.HasCompleted(ProductScope(), "Main"), Is.False);

        mgr.Track(ProductScope(), "Main", () => { });

        Assert.That(mgr.HasCompleted(ProductScope(), "Main"), Is.True);
    }

    [Test]
    public void HasCompletedScript_DatabaseAndProductScopes_MatchTrackScript()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var productScope = new TrackingScope { ProductName = "TestProduct", Server = "server1" };

        Assert.That(mgr.HasCompletedScript(DbScope(), "Before", "s.sql"), Is.False);
        Assert.That(mgr.HasCompletedScript(productScope, "Before", "p.sql"), Is.False);

        mgr.TrackScript(DbScope(), "Before", "s.sql", () => { });
        mgr.TrackScript(productScope, "Before", "p.sql", () => { });
        mgr.TrackScript(productScope, "After", "a.sql", () => { });

        Assert.That(mgr.HasCompletedScript(DbScope(), "Before", "s.sql"), Is.True);
        Assert.That(mgr.HasCompletedScript(DbScope(), "Before", "other.sql"), Is.False);
        Assert.That(mgr.HasCompletedScript(productScope, "Before", "p.sql"), Is.True);
        Assert.That(mgr.HasCompletedScript(productScope, "After", "a.sql"), Is.True);
    }

    [Test]
    public void GetProductCheckpointSummary_NoCheckpoint_ReturnsEmpty()
    {
        var mgr = new FileCheckpointManager(_tempDir);

        var summary = mgr.GetProductCheckpointSummary("NeverTracked");

        Assert.That(summary.HasAnyCompleted, Is.False);
        Assert.That(summary, Is.EqualTo(ProductCheckpointSummary.Empty));
    }

    [Test]
    public void GetProductCheckpointSummary_WithCompletions_CountsCorrectly()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        var scope1 = new TrackingScope { ProductName = "TestProduct", Server = "server1" };
        var scope2 = new TrackingScope { ProductName = "TestProduct", Server = "server2" };

        mgr.Track(ProductScope(), "Main", () => { });
        mgr.Track(ProductScope(), "Reporting", () => { });
        mgr.TrackScript(scope1, "Before", "b1.sql", () => { });
        mgr.TrackScript(scope1, "Before", "b2.sql", () => { });
        mgr.TrackScript(scope2, "Before", "b3.sql", () => { });
        mgr.TrackScript(scope1, "After", "a1.sql", () => { });

        var summary = mgr.GetProductCheckpointSummary("TestProduct");

        Assert.That(summary.HasAnyCompleted, Is.True);
        Assert.That(summary.CompletedTemplates, Is.EqualTo(2));
        Assert.That(summary.TotalBeforeScripts, Is.EqualTo(3));
        Assert.That(summary.ServersWithBeforeScripts, Is.EqualTo(2));
        Assert.That(summary.TotalAfterScripts, Is.EqualTo(1));
        Assert.That(summary.ServersWithAfterScripts, Is.EqualTo(1));
    }

    [Test]
    public void GetDatabaseCheckpointSummary_NoCheckpoint_ReturnsEmpty()
    {
        var mgr = new FileCheckpointManager(_tempDir);

        var summary = mgr.GetDatabaseCheckpointSummary(DbScope());

        Assert.That(summary.HasAnyCompleted, Is.False);
        Assert.That(summary, Is.EqualTo(DatabaseCheckpointSummary.Empty));
    }

    [Test]
    public void GetDatabaseCheckpointSummary_WithCompletions_CountsStepsAndScripts()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(DbScope(), "KindleForge", () => { });
        mgr.Track(DbScope(), "ValidateBaseline", () => { });
        mgr.TrackScript(DbScope(), "Before", "init.sql", () => { });
        mgr.TrackScript(DbScope(), "Object", "views/v1.sql", () => { });
        mgr.TrackScript(DbScope(), "Object", "views/v2.sql", () => { });

        var summary = mgr.GetDatabaseCheckpointSummary(DbScope());

        Assert.That(summary.HasAnyCompleted, Is.True);
        Assert.That(summary.CompletedSteps, Is.EqualTo(2));
        Assert.That(summary.TotalCompletedScripts, Is.EqualTo(3));
    }

    [Test]
    public void GetDatabaseCheckpointSummary_ProductScope_ReturnsEmpty()
    {
        var mgr = new FileCheckpointManager(_tempDir);
        mgr.Track(ProductScope(), "Main", () => { });

        var summary = mgr.GetDatabaseCheckpointSummary(ProductScope());

        Assert.That(summary, Is.EqualTo(DatabaseCheckpointSummary.Empty));
    }

    // Two runs at once that share a product, template, server name and database share these files. A run that is
    // not resuming must not take another run's progress for its own, or it skips steps it never ran and exits 0.
    [Test]
    public void Track_DatabaseScope_WhenNotResuming_RunsAStepAnotherRunRecorded()
    {
        new FileCheckpointManager(_tempDir, adoptExisting: false).Track(DbScope(), "ForeignKeys", () => { });

        var mgr = new FileCheckpointManager(_tempDir, adoptExisting: false);
        var ran = false;
        mgr.Track(DbScope(), "ForeignKeys", () => ran = true);

        Assert.That(ran, Is.True, "a step recorded by another run was skipped");
    }

    [Test]
    public void TrackScript_DatabaseScope_WhenNotResuming_RunsAScriptAnotherRunRecorded()
    {
        new FileCheckpointManager(_tempDir, adoptExisting: false).TrackScript(DbScope(), "After", "seed.sql", () => { });

        var mgr = new FileCheckpointManager(_tempDir, adoptExisting: false);
        var ran = false;
        mgr.TrackScript(DbScope(), "After", "seed.sql", () => ran = true);

        Assert.That(ran, Is.True, "a script recorded by another run was skipped");
    }

    [Test]
    public void Track_ProductScope_WhenNotResuming_RunsATemplateAnotherRunRecorded()
    {
        new FileCheckpointManager(_tempDir, adoptExisting: false).Track(ProductScope(), "Template:Main", () => { });

        var mgr = new FileCheckpointManager(_tempDir, adoptExisting: false);
        var ran = false;
        mgr.Track(ProductScope(), "Template:Main", () => ran = true);

        Assert.That(ran, Is.True, "a template recorded by another run was skipped");
    }

    [Test]
    public void CheckpointSummaries_WhenNotResuming_ReportNothingFromAnotherRun()
    {
        var other = new FileCheckpointManager(_tempDir, adoptExisting: false);
        other.Track(DbScope(), "ModifiedTables", () => { });
        other.Track(ProductScope(), "Template:Main", () => { });

        var mgr = new FileCheckpointManager(_tempDir, adoptExisting: false);

        Assert.That(mgr.GetDatabaseCheckpointSummary(DbScope()), Is.EqualTo(DatabaseCheckpointSummary.Empty));
        Assert.That(mgr.GetProductCheckpointSummary("TestProduct"), Is.EqualTo(ProductCheckpointSummary.Empty));
    }

    [Test]
    public void Track_WhenResuming_SkipsWhatTheEarlierRunCompleted()
    {
        new FileCheckpointManager(_tempDir, adoptExisting: false).Track(DbScope(), "ForeignKeys", () => { });

        var mgr = new FileCheckpointManager(_tempDir, adoptExisting: true);
        var ran = false;
        mgr.Track(DbScope(), "ForeignKeys", () => ran = true);

        Assert.That(ran, Is.False, "a resumed run repeated a completed step");
        Assert.That(mgr.GetDatabaseCheckpointSummary(DbScope()).CompletedSteps, Is.EqualTo(1));
    }

    // A16: a checkpoint from before 2.9 may have been written by a --WhatIf run, which recorded steps it never applied.
    // It is still honoured, so a legitimate resume across the upgrade keeps its progress, but the resume says so.
    [Test]
    public void ResumingFromACheckpointWrittenBefore29_WarnsThatAPreviewMayHaveWrittenIt()
    {
        var progress = NSubstitute.Substitute.For<log4net.ILog>();
        Schema.Utility.LogFactory.Register("ProgressLog", progress);
        try
        {
            new FileCheckpointManager(_tempDir, adoptExisting: false).Track(DbScope(), "ModifiedTables", () => { });
            var file = Directory.GetFiles(_tempDir, "*.checkpoint").Single();
            File.WriteAllLines(file, File.ReadAllLines(file).Where(l => !l.StartsWith("# Format:")));

            var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);
            Assert.That(resume.HasCompleted(DbScope(), "ModifiedTables"), Is.True, "the older checkpoint is still honoured");
            Assert.That(resume.HasCompleted(DbScope(), "IndexesAndConstraints"), Is.False);

            progress.Received(1).Warn(NSubstitute.Arg.Is<object>(o => o.ToString()!.Contains("before SchemaQuench 2.9") && o.ToString()!.Contains("--WhatIf")));
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    [Test]
    public void ResumingFromACurrentCheckpoint_DoesNotWarn()
    {
        var progress = NSubstitute.Substitute.For<log4net.ILog>();
        Schema.Utility.LogFactory.Register("ProgressLog", progress);
        try
        {
            new FileCheckpointManager(_tempDir, adoptExisting: false).Track(DbScope(), "ModifiedTables", () => { });
            Assert.That(File.ReadAllText(Directory.GetFiles(_tempDir, "*.checkpoint").Single()), Does.Contain("# Format: 2"));

            var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);
            Assert.That(resume.HasCompleted(DbScope(), "ModifiedTables"), Is.True);
            progress.DidNotReceive().Warn(NSubstitute.Arg.Any<object>());
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    // 1.4 run identity: a resumed run's summary names the run whose checkpoint it took up.
    [Test]
    public void AResume_ReportsTheRunIdTheCheckpointWasWrittenBy_AndTheFileThenCarriesTheResumingRunsId()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var writer = new FileCheckpointManager(_tempDir, adoptExisting: false);
        writer.StampRunId(first);
        writer.Track(DbScope(), "ModifiedTables", () => { });

        var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);
        resume.StampRunId(second);
        resume.Track(DbScope(), "ForeignKeys", () => { });

        Assert.That(resume.ResumedRunId, Is.EqualTo(first));
        Assert.That(File.ReadAllText(Directory.GetFiles(_tempDir, "*.checkpoint").Single()), Does.Contain($"# RunId: {second}"));
    }

    [Test]
    public void AResumeOfOnlyAProductCheckpoint_ReportsItsRunId()
    {
        var first = Guid.NewGuid();
        var writer = new FileCheckpointManager(_tempDir, adoptExisting: false);
        writer.StampRunId(first);
        writer.Track(ProductScope(), "Main", () => { });

        var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);
        resume.StampRunId(Guid.NewGuid());

        Assert.That(resume.HasCompleted(ProductScope(), "Main"), Is.True);
        Assert.That(resume.ResumedRunId, Is.EqualTo(first));
    }

    [Test]
    public void ACheckpointWrittenBefore29HasNoRunId_AndStillResumes_WithANullResumedRunId()
    {
        var writer = new FileCheckpointManager(_tempDir, adoptExisting: false);
        writer.StampRunId(Guid.NewGuid());
        writer.Track(DbScope(), "ModifiedTables", () => { });
        var file = Directory.GetFiles(_tempDir, "*.checkpoint").Single();
        File.WriteAllLines(file, File.ReadAllLines(file).Where(l => !l.StartsWith("# RunId:")));

        var resume = new FileCheckpointManager(_tempDir, adoptExisting: true);

        Assert.That(resume.HasCompleted(DbScope(), "ModifiedTables"), Is.True);
        Assert.That(resume.ResumedAny, Is.True);
        Assert.That(resume.ResumedRunId, Is.Null);
    }

    [Test]
    public void ARunThatIsNotResuming_HasNoResumedRunId()
    {
        var writer = new FileCheckpointManager(_tempDir, adoptExisting: false);
        writer.StampRunId(Guid.NewGuid());
        writer.Track(DbScope(), "ModifiedTables", () => { });

        var next = new FileCheckpointManager(_tempDir, adoptExisting: false);
        next.Track(DbScope(), "ModifiedTables", () => { });

        Assert.That(next.ResumedRunId, Is.Null);
    }
}
