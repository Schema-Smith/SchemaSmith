// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Table-level Change Tracking convergence, both directions, against a database where Change Tracking
/// IS enabled.
/// <para><b>Its own database on purpose.</b> Turning Change Tracking on is an <c>ALTER DATABASE</c> that
/// changes retention and cleanup for every table in it, so doing that to the shared SchemaQuench
/// integration database to serve one fixture would alter the conditions under which the other
/// ~1,400 tests run.</para>
/// <para>Not to be confused with the full-text index option spelled
/// <c>WITH CHANGE_TRACKING = AUTO|MANUAL|OFF</c>, which is unrelated and long implemented. A grep for
/// CHANGE_TRACKING finds that one and says this feature already exists.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ChangeTrackingDeployTests
{
    private IDbConnection _connection;
    private string _db;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaCtOn_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        Exec($"ALTER DATABASE [{_db}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON)");
        _connection.ChangeDatabase(_db);

        using var cmd = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Guards the premise: with the database toggle off, every enable below would degrade instead of
        // apply, and the assertions would fail for a reason that has nothing to do with what they test.
        Assert.That(Scalar("SELECT COUNT(*) FROM sys.change_tracking_databases WHERE database_id = DB_ID()"),
            Is.EqualTo(1), "this fixture's database MUST have Change Tracking enabled, or it tests nothing");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            _connection.ChangeDatabase("master");
            Exec($"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Exec($"DROP DATABASE IF EXISTS [{_db}]");
        }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private int Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
    }

    private static string Json(string table, string extra) => $$"""
        [{
            "Schema": "[dbo]",
            "Name": "[{{table}}]"{{extra}},
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [ { "Name": "[PK_{{table}}]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true } ]
        }]
        """;

    private void Deploy(string tableJson)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'CtTest', "
                          + $"@TableDefinitions = N'{tableJson.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
    }

    private int TrackedCount(string table) =>
        Scalar($"SELECT COUNT(*) FROM sys.change_tracking_tables WHERE [object_id] = OBJECT_ID('dbo.{table}')");

    private int ColumnsUpdatedFlag(string table) =>
        Scalar("SELECT CONVERT(INT, is_track_columns_updated_on) FROM sys.change_tracking_tables "
               + $"WHERE [object_id] = OBJECT_ID('dbo.{table}')");

    [Test]
    public void DeclaringChangeTracking_EnablesIt()
    {
        Deploy(Json("CtWanted", ", \"EnableChangeTracking\": true"));

        Assert.That(TrackedCount("CtWanted"), Is.EqualTo(1),
            "the declared feature has to actually be on -- a green run with no tracking is the CDC defect "
            + "this whole pattern exists to avoid");
    }

    [Test]
    public void DeclaringChangeTracking_IsIdempotent()
    {
        // The second deploy is the one that finds bugs. A convergence written as "enable when asked"
        // rather than "enable when asked AND not already on" errors 4997 here.
        Deploy(Json("CtTwice", ", \"EnableChangeTracking\": true"));
        Deploy(Json("CtTwice", ", \"EnableChangeTracking\": true"));

        Assert.That(TrackedCount("CtTwice"), Is.EqualTo(1), "re-deploying an already-tracked table must be a no-op");
    }

    [Test]
    public void TrackColumnsUpdated_IsHonoured()
    {
        Deploy(Json("CtColumns", ", \"EnableChangeTracking\": true, \"TrackColumnsUpdated\": true"));

        Assert.Multiple(() =>
        {
            Assert.That(TrackedCount("CtColumns"), Is.EqualTo(1));
            Assert.That(ColumnsUpdatedFlag("CtColumns"), Is.EqualTo(1),
                "TRACK_COLUMNS_UPDATED is the one option worth authoring, so declaring it has to reach the "
                + "ALTER -- not merely be accepted and dropped");
        });
    }

    [Test]
    public void TrackColumnsUpdated_ConvergesWhenTheDeclarationChanges()
    {
        // Asserts the OUTCOME a user would notice (the option is now on) rather than that some ALTER ran.
        // A convergence that only ever enables tracking, never reconciles the option, passes the tests
        // above and fails this one.
        Deploy(Json("CtFlip", ", \"EnableChangeTracking\": true"));
        Assert.That(ColumnsUpdatedFlag("CtFlip"), Is.Zero, "precondition: starts without the option");

        Deploy(Json("CtFlip", ", \"EnableChangeTracking\": true, \"TrackColumnsUpdated\": true"));

        Assert.That(ColumnsUpdatedFlag("CtFlip"), Is.EqualTo(1),
            "changing the declaration must change the table -- otherwise the package and the database "
            + "disagree and nothing says so");
    }

    [Test]
    public void DeclaringFalse_DisablesIt()
    {
        Deploy(Json("CtOff", ", \"EnableChangeTracking\": true"));
        Assert.That(TrackedCount("CtOff"), Is.EqualTo(1), "precondition: tracking is on");

        Deploy(Json("CtOff", ", \"EnableChangeTracking\": false"));

        Assert.That(TrackedCount("CtOff"), Is.Zero, "an explicit false must turn tracking off");
    }

    // Unset is unmanaged. Tracking a DBA turned on, or that a package written before the declaration existed never
    // mentions, must survive a deploy: disabling it discards the baseline every consumer synchronizes from.
    [Test]
    public void AnUndeclaredTable_KeepsTrackingTurnedOnOutsideThePackage()
    {
        Deploy(Json("CtOutside", ""));
        Exec("ALTER TABLE [dbo].[CtOutside] ENABLE CHANGE_TRACKING");
        Assert.That(TrackedCount("CtOutside"), Is.EqualTo(1), "precondition: tracking is on");

        Deploy(Json("CtOutside", ""));

        Assert.That(TrackedCount("CtOutside"), Is.EqualTo(1),
            "a table that does not declare EnableChangeTracking must keep the tracking it has");
    }

    [Test]
    public void ATableThatNeverAsked_IsNotTracked()
    {
        // The negative half. Without it, a convergence that enabled tracking for every table would pass
        // every assertion above.
        Deploy(Json("CtNeverAsked", ""));

        Assert.That(TrackedCount("CtNeverAsked"), Is.Zero);
    }
}
