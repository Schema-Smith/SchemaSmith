// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Extraction against a read-only database — an Availability Group readable secondary being the case
/// that matters, since it is the copy people are actually allowed to hammer.
/// <para>SchemaTongs kindles the SchemaSmith helper procedures into the <b>source</b> database on every
/// run, which needs write access and so cannot work against a secondary at all. Extraction only ever
/// reads those helpers, so on a read-only target it verifies instead of kindling: helpers missing is a
/// hard error, helpers present but stale is a warning, and helpers present but unverifiable is also a
/// warning that says so plainly.</para>
/// <para>A database <c>SET READ_ONLY</c> reports through exactly the same
/// <c>DATABASEPROPERTYEX(…, 'Updateability')</c> check a readable secondary does, so this fixture
/// certifies the real code path without needing an Availability Group.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ReadOnlyTargetKindleTests
{
    private string _masterConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _masterConnectionString = FixtureSetup.GetMainDbConnectionString();






    private IDbConnection OpenMaster()
    {
        var c = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        c.Open();
        return c;
    }

    private static void Exec(IDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates a database, optionally kindles it, optionally corrupts its stamp, then makes it read-only.</summary>
    private string MakeDatabase(bool kindle, bool staleStamp = false)
    {
        var db = $"SchemaRo_{Guid.NewGuid():N}"[..38];
        using (var master = OpenMaster())
        {
            Exec(master, $"CREATE DATABASE [{db}]");
        }

        if (kindle)
        {
            using var c = OpenMaster();
            c.ChangeDatabase(db);
            using var cmd = c.CreateCommand();
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
            if (staleStamp)
                Exec(c, "UPDATE SchemaSmith.KindleStamp SET Stamp = 'stale-on-purpose'");
        }

        using (var master = OpenMaster())
        {
            Exec(master, $"ALTER DATABASE [{db}] SET READ_ONLY WITH ROLLBACK IMMEDIATE");
        }
        return db;
    }

    private void DropDatabase(string db)
    {
        using var master = OpenMaster();
        try
        {
            // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            Exec(master, $"ALTER DATABASE [{db}] SET READ_WRITE WITH ROLLBACK IMMEDIATE");
            using var cmd = master.CreateCommand();
            cmd.CommandTimeout = 300;
            TestDatabaseDrop.Drop(cmd, db);
        }
        catch
        {
            // Teardown of a throwaway database must never mask the assertion that already ran.
        }
    }

    private string StampOn(string db)
    {
        using var c = OpenMaster();
        c.ChangeDatabase(db);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 [Stamp] FROM [SchemaSmith].[KindleStamp]";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private void OnTheDatabase(string db, Action<IDbCommand> act)
    {
        using var c = OpenMaster();
        c.ChangeDatabase(db);
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    [Test]
    public void AReadOnlyTarget_WithCurrentHelpers_ProceedsWithoutKindlingOrWarning()
    {
        var db = MakeDatabase(kindle: true);
        try
        {
            OnTheDatabase(db, cmd =>
                Assert.DoesNotThrow(
                    () => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, allowReadOnlyTarget: true),
                    "extraction against a readable secondary is the whole point; kindling must be skipped "
                    + "rather than attempted, and a write attempt here fails outright"));

            Assert.That(StampOn(db), Is.Not.Empty,
                "the helpers must be left exactly as they were -- verification reads, it never writes");
        }
        finally { DropDatabase(db); }
    }

    [Test]
    public void AReadOnlyTarget_WithNoHelpers_FailsHard()
    {
        var db = MakeDatabase(kindle: false);
        try
        {
            var ex = Assert.Catch(() => OnTheDatabase(db, cmd =>
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, allowReadOnlyTarget: true)));

            Assert.That(ex, Is.Not.Null,
                "there is nothing to extract with -- proceeding would fail later with a confusing "
                + "'could not find stored procedure', or worse, silently produce nothing");
            Assert.That(ex.Message, Does.Contain("read-only").IgnoreCase,
                "the message must say why SchemaSmith did not simply fix it up");
            Assert.That(ex.Message, Does.Contain("primary").IgnoreCase,
                "and must name the remedy -- kindle on the primary and let it replicate");
        }
        finally { DropDatabase(db); }
    }

    [Test]
    public void AReadOnlyTarget_WithStaleHelpers_WarnsAndProceeds()
    {
        var db = MakeDatabase(kindle: true, staleStamp: true);
        try
        {
            OnTheDatabase(db, cmd =>
                Assert.DoesNotThrow(
                    () => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, allowReadOnlyTarget: true),
                    "stale helpers still extract -- refusing would make a secondary useless the moment "
                    + "the primary is a version ahead, which is most of the time"));

            Assert.That(StampOn(db), Is.EqualTo("stale-on-purpose"),
                "the stale stamp must survive untouched. If verification ever tried to correct it, the "
                + "write would fail on a read-only target -- and on a writable one it would paper over "
                + "the very staleness the warning exists to report.");
        }
        finally { DropDatabase(db); }
    }

    [Test]
    public void AWritableTarget_StillKindlesNormally_EvenWhenReadOnlyTargetsAreAllowed()
    {
        // The negative half. Without it, a change that treated every target as read-only would pass
        // every assertion above while quietly never kindling anything again.
        var db = $"SchemaRw_{Guid.NewGuid():N}"[..38];
        using (var master = OpenMaster()) Exec(master, $"CREATE DATABASE [{db}]");
        try
        {
            OnTheDatabase(db, cmd =>
            {
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, allowReadOnlyTarget: true);

                cmd.CommandText = "SELECT COUNT(*) FROM sys.objects WHERE name = 'GenerateTableJson' AND type = 'P'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "a writable target must still be kindled -- allowReadOnlyTarget relaxes what happens "
                    + "on a read-only target, it does not turn kindling off");
            });
        }
        finally
        {
            using var master = OpenMaster();
            using var cmd = master.CreateCommand();
            cmd.CommandTimeout = 300;
            TestDatabaseDrop.Drop(cmd, db);
        }
    }
}
