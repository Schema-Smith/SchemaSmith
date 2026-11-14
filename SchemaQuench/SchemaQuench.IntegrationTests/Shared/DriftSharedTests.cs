// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// H1, through a real run of the whole product: --Drift exits 0 on a target that matches its package and 5 on one that
/// does not, naming what differs, and leaves the target as it found it. What is asserted is what a pipeline acts on: the
/// exit code, the verdict and the object named.
/// </summary>
[NonParallelizable]
public abstract class DriftSharedTests : ObjectScriptDeployFixture
{
    /// <summary>A table named <paramref name="table"/> with an id key and the given extra columns, all int.</summary>
    protected abstract string TableJson(string table, params string[] columns);
    protected abstract string TableFileName(string table);
    /// <summary>The name the change audit gives a column of the table.</summary>
    protected abstract string QualifiedColumn(string table, string column);
    protected abstract string DropTable(string table);
    protected abstract string DropColumn(string table, string column);
    /// <summary>A view script that returns <paramref name="value"/>, written so it can run again.</summary>
    protected abstract string ViewScript(string view, int value);
    protected abstract string DropView(string view);
    protected abstract string StampTable { get; }
    /// <summary>SQL Server and PostgreSQL kindle inside a rolled-back transaction; MySQL and MariaDB cannot.</summary>
    protected virtual bool KindlesInATransaction => true;

    protected const string Table = "drift_t";
    private const string View = "drift_v";

    protected static readonly Dictionary<string, string> NoKindle = new() { ["KindleTheForge"] = "false" };

    [SetUp]
    public void ADeployedTable()
    {
        Exec(DropTable(Table));
        WritePackage("");
        WriteFile("Tables", TableFileName(Table), TableJson(Table, "a", "b"));
        Deploy();
    }

    [TearDown]
    public void DropIt()
    {
        Exec(DropView(View));
        Exec(DropTable(Table));
    }

    private void MakeTheHelpersLookStale() => Exec($"UPDATE {StampTable} SET {Q("Stamp")} = 'stale'");

    private string Q(string name) => Platform == Schema.Domain.Platform.SqlServer ? $"[{name}]" : Platform == Schema.Domain.Platform.PostgreSQL ? $"\"{name}\"" : name;

    private string StampNow() => Scalar($"SELECT {Q("Stamp")} FROM {StampTable}")?.ToString();

    private static string Unquoted(string name) => name?.Replace("[", "").Replace("]", "").Replace("`", "").Replace("\"", "");

    private static void AssertClean(DriftRun run)
    {
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
            Assert.That(run.Report?.Drifted, Is.False, run.Describe());
        });
    }

    private void AssertTheDroppedColumnIsDrift(DriftRun run)
    {
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(5), run.Describe());
            Assert.That(run.Report?.Targets.Single().Status, Is.EqualTo("drifted"), run.Describe());
            Assert.That(run.Report?.Targets.Single().Changes.Select(c => Unquoted(c.ObjectName)), Does.Contain(QualifiedColumn(Table, "b")), run.Describe());
            Assert.That(ColumnBExists(), Is.False, "a drift check changes nothing: the column is still missing");
        });
    }

    private bool ColumnBExists()
    {
        var schema = Platform switch
        {
            Schema.Domain.Platform.SqlServer => "'dbo'",
            Schema.Domain.Platform.PostgreSQL => "'public'",
            _ => "DATABASE()"
        };
        return Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = {schema} AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'b'")) > 0;
    }

    [Test]
    public void Tier0_AMatchingTargetIsClean_AndAColumnDroppedOutOfBandIsDrift()
    {
        var clean = RunDrift(settings: NoKindle);
        AssertClean(clean);
        Assert.That(clean.DriftTier, Is.EqualTo(0));

        Exec(DropColumn(Table, "b"));
        AssertTheDroppedColumnIsDrift(RunDrift(settings: NoKindle));
    }

    // A database the deploy would create does not exist: the largest drift there is, not a clean target. (Guards the
    // provisioner's wouldCreate row, which is what drift counts here; the run creates nothing.)
    [Test]
    public void ADatabaseTheDeployWouldCreate_IsDrift()
    {
        var missing = $"drift_missing_{System.Guid.NewGuid().ToString("N")[..8]}";
        var run = RunDrift(settings: new Dictionary<string, string>(NoKindle)
        {
            ["Target:TemplateTargets:Main:Databases:0"] = missing,
            ["Target:TemplateTargets:Main:CreateIfMissing"] = "true",
        });

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(5), run.Describe());
            Assert.That(run.Report?.Targets.Single().Status, Is.EqualTo("drifted"), run.Describe());
        });
    }

    [Test]
    public void Tier1_AMatchingTargetIsClean_AndAColumnDroppedOutOfBandIsDrift()
    {
        if (!KindlesInATransaction) Assert.Ignore("MySQL and MariaDB have no tier 1.");
        var clean = RunDrift();
        AssertClean(clean);
        Assert.That(clean.DriftTier, Is.EqualTo(1));

        Exec(DropColumn(Table, "b"));
        AssertTheDroppedColumnIsDrift(RunDrift());
    }

    // A database SchemaSmith never deployed to: no helpers, no ownership, none of the package's tables. Tier 1 kindles
    // there inside the transaction it rolls back, so the table to create is drift and the database is left with nothing
    // of SchemaSmith's in it.
    [Test]
    public void Tier1_ADatabaseNeverDeployedTo_IsDrift_AndKeepsNothing()
    {
        if (!KindlesInATransaction) Assert.Ignore("MySQL and MariaDB have no tier 1.");
        var fresh = $"drift_fresh_{Guid.NewGuid().ToString("N")[..8]}";
        Exec(Platform == Schema.Domain.Platform.SqlServer ? $"CREATE DATABASE [{fresh}]" : $"CREATE DATABASE \"{fresh}\"");
        try
        {
            var run = RunDrift(settings: new Dictionary<string, string> { ["Target:TemplateTargets:Main:Databases:0"] = fresh });

            Assert.Multiple(() =>
            {
                Assert.That(run.DriftTier, Is.EqualTo(1), run.Describe());
                Assert.That(run.ExitCode, Is.EqualTo(5), run.Describe());
                Assert.That(run.Report?.Targets.Single().Status, Is.EqualTo("drifted"), run.Describe());
                Assert.That(run.Report?.Targets.Single().Changes.Select(c => Unquoted(c.ObjectName)), Has.Some.EndsWith(Table), run.Describe());
                Assert.That(SchemaSmithObjectsIn(fresh), Is.Zero, "the kindle was rolled back with the rest");
            });
        }
        finally
        {
            DropDatabase(fresh);
        }
    }

    private long SchemaSmithObjectsIn(string database)
    {
        if (Platform == Schema.Domain.Platform.SqlServer)
            return Count($"SELECT (SELECT COUNT(*) FROM [{database}].sys.schemas WHERE name = 'SchemaSmith') + "
                         + $"(SELECT COUNT(*) FROM [{database}].sys.objects o JOIN [{database}].sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = 'SchemaSmith')");
        using var connection = Schema.DataAccess.DbConnectionFactory.ForPlatform(Platform).GetDbConnection(TestConnection(database));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM pg_namespace WHERE nspname = 'SchemaSmith') + "
                          + "(SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'SchemaSmith')";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void DropDatabase(string database)
    {
        if (Platform == Schema.Domain.Platform.SqlServer)
        {
            using var master = Schema.DataAccess.DbConnectionFactory.ForPlatform(Platform).GetDbConnection(TestConnection("master"));
            master.Open();
            using var cmd = master.CreateCommand();
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, database);
            return;
        }
        Npgsql.NpgsqlConnection.ClearAllPools();
        Exec($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid()");
        Exec($"DROP DATABASE IF EXISTS \"{database}\"");
    }

    // The kindle runs (the stamp says the helpers are out of date) and is rolled back with everything else.
    [Test]
    public void Tier1_LeavesTheTargetAsItFoundIt()
    {
        if (!KindlesInATransaction) Assert.Ignore("MySQL and MariaDB have no tier 1.");
        MakeTheHelpersLookStale();
        try
        {
            AssertClean(RunDrift());
            Assert.That(StampNow(), Is.EqualTo("stale"), "the kindle the drift check ran was rolled back");
        }
        finally
        {
            Deploy();
        }
    }

    [Test]
    public void Tier0_HelpersOutOfDate_FailTheTargetRatherThanAnswer()
    {
        MakeTheHelpersLookStale();
        try
        {
            var run = RunDrift(settings: NoKindle);
            Assert.Multiple(() =>
            {
                Assert.That(run.ExitCode, Is.EqualTo(2), run.Describe());
                Assert.That(run.Report?.Targets.Single().Status, Is.EqualTo("failed"), run.Describe());
                Assert.That(run.Log, Has.Some.Contains("out of date"), run.Describe());
            });
        }
        finally
        {
            Deploy();
        }
    }

    [Test]
    public void AViewChangedOutOfBand_IsDrift()
    {
        Exec(DropView(View));
        WritePackage("""{ "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" }""", "Views");
        WriteFile("Tables", TableFileName(Table), TableJson(Table, "a", "b"));
        WriteFile("Views", $"{View}.sql", ViewScript(View, 1));
        Deploy();
        AssertClean(RunDrift(settings: NoKindle));

        Exec(ViewScript(View, 2));
        var run = RunDrift(settings: NoKindle);
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(5), run.Describe());
            Assert.That(run.Report?.Targets.Single().Changes.Select(c => c.ObjectType), Does.Contain("modules"), run.Describe());
        });
    }

    // A script no deploy can stamp (here one in an object folder that names no module) runs on every deploy, so a drift
    // check leaves it out as it does [ALWAYS] scripts: it does not make a matching target unverifiable on every run.
    [Test]
    public void AScriptNoDeployCanStamp_IsLeftOut_AndAMatchingTargetStaysClean()
    {
        WritePackage("""{ "FolderPath": "Misc", "QuenchSlot": "Objects" }""", "Misc");
        WriteFile("Tables", TableFileName(Table), TableJson(Table, "a", "b"));
        WriteFile("Misc", "every-deploy.sql", "SELECT 1");
        Deploy();

        var run = RunDrift(settings: NoKindle);
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
            Assert.That(run.Report?.Unverifiable, Is.Empty, run.Describe());
            Assert.That(run.Report?.Targets.Single().Status, Is.EqualTo("clean"), run.Describe());
        });
    }

    // A run-once script not yet run means this release is not deployed yet: reported, and counted only when asked.
    [Test]
    public void APendingMigration_IsReported_AndCountsOnlyWhenFailOnNamesIt()
    {
        WritePackage("""{ "FolderPath": "Before Scripts", "QuenchSlot": "Before" }""", "Before Scripts");
        WriteFile("Tables", TableFileName(Table), TableJson(Table, "a", "b"));
        WriteFile("Before Scripts", "first.sql", "SELECT 1");
        Deploy();
        WriteFile("Before Scripts", "second.sql", "SELECT 2");

        var run = RunDrift(settings: NoKindle);
        var counted = RunDrift("--Drift --DriftFailOn:migrations", NoKindle);
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
            Assert.That(run.Report?.PendingMigrations.Select(m => m.ObjectName), Has.Some.EndsWith("second.sql").And.None.EndsWith("first.sql"), run.Describe());
            Assert.That(counted.ExitCode, Is.EqualTo(5), counted.Describe());
        });
    }

    [Test]
    public void DriftAndWhatIfTogether_AreRefused()
    {
        var run = RunDrift("--Drift --WhatIf", NoKindle);
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(2));
            Assert.That(run.Refusal, Does.Contain("--WhatIf"));
        });
    }

    [Test]
    public void WithKindlingOn_AnEngineWithoutTier1_IsRefusedNamingTheWaysOut()
    {
        if (KindlesInATransaction) Assert.Ignore("SQL Server and PostgreSQL kindle in a transaction.");
        var run = RunDrift();
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(2));
            Assert.That(run.Refusal, Does.Contain("KindleTheForge").And.Contain("Target:DriftHelperDatabase"));
        });
    }

    // Tier 2 (MySQL, MariaDB): the helpers live in a database of their own, which the test makes and drops.
    private string MakeHelperDatabase()
    {
        var name = $"drift_helper_{Guid.NewGuid():N}"[..24];
        Exec($"CREATE DATABASE `{name}`");
        return name;
    }

    private static Dictionary<string, string> Helper(string database) => new() { ["Target:DriftHelperDatabase"] = database };

    [Test]
    public void Tier2_AMatchingTargetIsClean_AndAColumnDroppedOutOfBandIsDrift()
    {
        if (KindlesInATransaction) Assert.Ignore("Tier 2 is for MySQL and MariaDB.");
        var helper = MakeHelperDatabase();
        try
        {
            var clean = RunDrift(settings: Helper(helper));
            AssertClean(clean);
            Assert.That(clean.DriftTier, Is.EqualTo(2));

            Exec(DropColumn(Table, "b"));
            AssertTheDroppedColumnIsDrift(RunDrift(settings: Helper(helper)));
        }
        finally
        {
            Exec($"DROP DATABASE IF EXISTS `{helper}`");
        }
    }

    // The kindle goes to the helper database, and the target's stamps, run migrations and ownership come with it: a
    // stamped view, a migration already run and an owned table all read as they are on the target.
    [Test]
    public void Tier2_KindlesIntoTheHelper_AndReadsTheTargetsState()
    {
        if (KindlesInATransaction) Assert.Ignore("Tier 2 is for MySQL and MariaDB.");
        Exec(DropView(View));
        WritePackage("""{ "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" }, { "FolderPath": "Before Scripts", "QuenchSlot": "Before" }""",
            "Views", "Before Scripts");
        WriteFile("Tables", TableFileName(Table), TableJson(Table, "a", "b"));
        WriteFile("Views", $"{View}.sql", ViewScript(View, 1));
        WriteFile("Before Scripts", "first.sql", "SELECT 1");
        Deploy();
        MakeTheHelpersLookStale();
        // So only the helper database's copy can answer.
        Exec($"DROP PROCEDURE IF EXISTS `{MainDb}`.SchemaSmith_ModifiedTableQuench");
        var helper = MakeHelperDatabase();
        try
        {
            var run = RunDrift(settings: Helper(helper));
            Assert.Multiple(() =>
            {
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
                Assert.That(run.Report?.PendingMigrations, Is.Empty, run.Describe());
                Assert.That(run.Report?.Unverifiable, Is.Empty, run.Describe());
                Assert.That(StampNow(), Is.EqualTo("stale"), "nothing was kindled into the target");
                Assert.That(Count($"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{helper}'"), Is.GreaterThan(0),
                    "the helpers went to the helper database");
            });
        }
        finally
        {
            Exec($"DROP DATABASE IF EXISTS `{helper}`");
            Deploy();
        }
    }

    [Test]
    public void Tier2_AHelperDatabaseThatIsNotThere_FailsTheTargetSayingSo()
    {
        if (KindlesInATransaction) Assert.Ignore("Tier 2 is for MySQL and MariaDB.");
        var run = RunDrift(settings: Helper("drift_helper_missing"));
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(2), run.Describe());
            Assert.That(run.Log, Has.Some.Contains("drift_helper_missing").And.Some.Contains("does not create it"), run.Describe());
        });
    }
}
