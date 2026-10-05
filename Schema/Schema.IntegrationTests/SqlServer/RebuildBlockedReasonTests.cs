// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// SchemaSmith.fn_RebuildBlockedReason on SQL Server: the guard that decides whether a table may be
/// replaced by a shadow copy, and names the live state that forbids it when it may not.
///
/// Each state below carries data or an external relationship that lives OUTSIDE the schema package, so a
/// copy-and-swap destroys it silently and no re-deploy can put it back. Getting the ANSWER right is only
/// half of it -- the operator has to be told WHICH state blocked them, or they cannot decide what to write
/// instead. Every assertion here therefore pins the named state, not merely "something was returned".
///
/// This fixture runs in its own database. Change Data Capture and Change Tracking are DATABASE-scoped
/// switches, and sp_cdc_enable_table writes database-wide replication metadata; turning either on inside the
/// shared integration database would leak into every sibling fixture and (per
/// SchemaQuench.IntegrationTests.SqlServer.TableQuench_CDCTests) invites cross-fixture deadlocks.
///
/// NOT covered here: sys.tables.is_replicated. Marking a table as a replication article needs a configured
/// distributor and a running SQL Server Agent, neither of which exists in the Linux CI container, and the
/// undocumented sp_MS* shortcuts that fake the flag are not a state a user can reach. The predicate is in
/// the function; this suite does not prove it fires.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class RebuildBlockedReasonTests
{
    private IDbConnection _connection = null!;
    private string _db = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaRebuildGuard_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        // Turned on from master, before anything connects into the database.
        Exec($"ALTER DATABASE [{_db}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON)");
        _connection.ChangeDatabase(_db);

        using (var cmd = _connection.CreateCommand())
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Deliberately kindled with NO detected server major version (serverMajorVersion defaults to 0), which
        // is what several SchemaTongs kindle paths do. The temporal predicate must still be compiled into the
        // function on this modern server -- if the kindle-time gate read the raw baked token instead of
        // SchemaSmith.fn_ServerMajorVersion(), "0 >= 13" would be false and
        // SystemVersionedTemporalTable_IsBlocked_AndTheReasonNamesSystemVersioning below would fail with a
        // NULL reason on a table that plainly is system-versioned.

        // Mirrors SchemaQuench.IntegrationTests.SqlServer.FixtureSetup: enabled from inside the database,
        // after kindling, which is the sequence already proven in CI.
        Exec("EXEC sys.sp_cdc_enable_db");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection != null)
        {
            try
            {
                _connection.ChangeDatabase("master");
                // Retried on deadlock. This fixture enables CDC and Change Tracking, both of which write
                // server-wide replication metadata, so tearing its database down collides with any sibling
                // fixture doing DDL at the same moment. Seen once in a full concurrent gate: every assembly
                // reported 0 failures and the RUN still exited non-zero, because an NUnit TearDown failure
                // fails the run without ever being counted as a failed test -- which is exactly why this is
                // worth retrying rather than leaving as a rare confusing red.
                // One batch: between two separate calls a CDC or Change Tracking cleanup task can take the
                // single-user slot, and the DROP then fails with "currently in use".
                ExecWithDeadlockRetry($"IF DB_ID('{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END");
            }
            finally
            {
                _connection.Close();
                _connection.Dispose();
            }
        }
    }

    // ---- helpers -----------------------------------------------------------

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Teardown-only. Matches on the deadlock TEXT rather than an error number: SQL Server wraps 1205
    /// inside other errors on some paths (a CDC enable reports it under 22832), so a number check can
    /// miss it. After the retries it runs once unguarded, so a genuine failure surfaces with its real
    /// error rather than a synthesized one.
    /// </summary>
    private void ExecWithDeadlockRetry(string sql)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Exec(sql);
                return;
            }
            catch (DbException e) when (e.Message.ContainsIgnoringCase("deadlock victim") ||
                                        e.Message.ContainsIgnoringCase("because it is currently in use"))
            {
                Thread.Sleep(1000);
            }
        }

        Exec(sql);
    }

    private string BlockedReason(string table, string schema = "dbo")
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT SchemaSmith.fn_RebuildBlockedReason('{schema}', '{table}')";
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private int ServerMajorVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT SchemaSmith.fn_ServerMajorVersion()";
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    // ---- a table with no special state is rebuildable -----------------------
    //
    // No per-test cleanup: every table name here is unique to one test and the whole database is dropped in
    // OneTimeTearDown. A finally-block calling sp_cdc_disable_table or SET SYSTEM_VERSIONING = OFF could fail
    // in its own right and would then mask the assertion failure that actually matters.

    [Test]
    public void PlainTable_IsRebuildable()
    {
        Exec("CREATE TABLE dbo.PlainRebuildGuard (Id INT NOT NULL PRIMARY KEY, Val NVARCHAR(50) NULL)");

        Assert.That(BlockedReason("PlainRebuildGuard"), Is.Null,
            "An ordinary table holds nothing a shadow copy would lose, so it must be allowed to rebuild. A "
            + "false block here would push the user to hand-written migration scripts for a table that never "
            + "needed them.");
    }

    [Test]
    public void UnknownTable_IsNotReportedAsBlocked()
    {
        Assert.That(BlockedReason("NoSuchTableAnywhere"), Is.Null,
            "A table that does not exist has nothing to rebuild, so the guard must not invent a blocking "
            + "state for it -- the caller decides what a missing table means.");
    }

    // ---- system-versioned temporal (the version-gated predicate) ------------

    [Test]
    public void SystemVersionedTemporalTable_IsBlocked_AndTheReasonNamesSystemVersioning()
    {
        if (ServerMajorVersion() < 13)
            Assert.Ignore("System-versioned temporal tables are SQL Server 2016+; the state cannot exist on "
                          + "this server, which is exactly why the predicate is omitted from the function here.");

        Exec(@"
CREATE TABLE dbo.TemporalRebuildGuard (
  Id INT NOT NULL PRIMARY KEY,
  Val NVARCHAR(50) NULL,
  SysStart DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
  SysEnd DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
  PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TemporalRebuildGuard_Hist))");

        var reason = BlockedReason("TemporalRebuildGuard");

        // Asserted non-null first: Does.Contain against null reports "Expected: IEnumerable But was: null",
        // which names nothing and would leave a silent gate looking like an ordinary text mismatch.
        Assert.That(reason, Is.Not.Null,
            "A system-versioned table must be refused a rebuild: the copy-and-swap leaves the history table "
            + "behind and every historical row the table has ever captured becomes unreachable. A NULL here is "
            + "the version gate having compiled the pre-2016 body onto a 2016+ server.");
        Assert.That(reason, Does.Contain("system versioning"),
            $"The refusal must name system versioning so the operator knows to reach for a Before/After "
            + $"migration script rather than guessing why the deploy stopped. Got: '{reason}'.");
    }

    // ---- Change Data Capture ------------------------------------------------

    [Test]
    public void CdcTrackedTable_IsBlocked_AndTheReasonNamesChangeDataCapture()
    {
        Exec("CREATE TABLE dbo.CdcRebuildGuard (Id INT NOT NULL PRIMARY KEY, Val NVARCHAR(50) NULL)");
        Exec("EXEC sys.sp_cdc_enable_table @source_schema = 'dbo', @source_name = 'CdcRebuildGuard', @role_name = NULL");

        var reason = BlockedReason("CdcRebuildGuard");

        Assert.That(reason, Is.Not.Null,
            "A CDC-tracked table must be refused a rebuild: dropping the source drops its capture instance and "
            + "the change table with it, so every change not yet consumed by a downstream reader is lost with "
            + "no error anywhere.");
        Assert.That(reason, Does.Contain("Change Data Capture"),
            $"The refusal must name Change Data Capture -- 'this table cannot be rebuilt' with no state named "
            + $"leaves the operator no way to know what to disable or migrate. Got: '{reason}'.");
    }

    // ---- Change Tracking ----------------------------------------------------

    [Test]
    public void ChangeTrackedTable_IsBlocked_AndTheReasonNamesChangeTracking()
    {
        Exec("CREATE TABLE dbo.CtRebuildGuard (Id INT NOT NULL PRIMARY KEY, Val NVARCHAR(50) NULL)");
        Exec("ALTER TABLE dbo.CtRebuildGuard ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF)");

        var reason = BlockedReason("CtRebuildGuard");

        Assert.That(reason, Is.Not.Null,
            "A change-tracked table must be refused a rebuild: the swap resets the tracking baseline, so a sync "
            + "client asking for changes since its last version silently gets an incomplete answer instead of "
            + "an error telling it to re-initialise.");
        Assert.That(reason, Does.Contain("Change Tracking"),
            $"The refusal must name Change Tracking, and must not be confused with Change Data Capture -- they "
            + $"are disabled differently. Got: '{reason}'.");
    }
    // ---- partitioning -------------------------------------------------------

    [Test]
    public void PartitionedTable_IsBlocked_AndTheReasonNamesPartitioning()
    {
        Exec("CREATE PARTITION FUNCTION pfRebuildGuard (INT) AS RANGE RIGHT FOR VALUES (100, 200)");
        Exec("CREATE PARTITION SCHEME psRebuildGuard AS PARTITION pfRebuildGuard ALL TO ([PRIMARY])");
        Exec("CREATE TABLE dbo.PartRebuildGuard (Id INT NOT NULL, Val NVARCHAR(50) NULL) ON psRebuildGuard(Id)");

        var reason = BlockedReason("PartRebuildGuard");

        Assert.That(reason, Is.Not.Null,
            "A partitioned table must be refused a rebuild. The shadow CREATE TABLE carries no placement "
            + "clause at all, so the copy lands on the default filegroup: every row survives and the layout "
            + "the table existed for -- the sliding window, the per-partition filegroups -- is gone, with no "
            + "error anywhere and nothing in the package able to put it back. PostgreSQL's twin of this "
            + "function already refuses; this engine must too.");
        Assert.That(reason, Does.Contain("partition"),
            $"The refusal must name partitioning, or the operator cannot tell which state to migrate around. "
            + $"Got: '{reason}'.");
    }

    [Test]
    public void PartitionAlignedIndexOnAnOtherwisePlainTable_IsBlocked()
    {
        // The table is a heap on the DEFAULT filegroup -- only its nonclustered index is partitioned. The
        // rebuild drops the old table whole and the ordinary index passes re-add from the package, so the
        // index alignment is lost the same way. A guard that reads only the table's own data space misses
        // this, which is why the check looks at every index.
        // Its own function and scheme: NUnit does not guarantee test order, so borrowing the pair the
        // sibling test creates would make this pass or fail on execution order rather than on the guard.
        Exec("CREATE PARTITION FUNCTION pfRebuildGuardIdx (INT) AS RANGE RIGHT FOR VALUES (100, 200)");
        Exec("CREATE PARTITION SCHEME psRebuildGuardIdx AS PARTITION pfRebuildGuardIdx ALL TO ([PRIMARY])");
        Exec("CREATE TABLE dbo.PartIdxRebuildGuard (Id INT NOT NULL, Val NVARCHAR(50) NULL)");
        Exec("CREATE NONCLUSTERED INDEX ixPartRebuildGuard ON dbo.PartIdxRebuildGuard(Id) ON psRebuildGuardIdx(Id)");

        var reason = BlockedReason("PartIdxRebuildGuard");

        // Presence first: Does.Contain against a null reports "Expected: IEnumerable But was: null", which
        // names neither the table nor the state and reads as a test bug rather than the defect.
        Assert.That(reason, Is.Not.Null,
            "An index aligned to a partition scheme is destroyed by the rebuild just as surely as a "
            + "partitioned heap, and the package cannot restore it.");
        Assert.That(reason, Does.Contain("partition"),
            $"The refusal must name partitioning. Got: '{reason}'.");
    }
}
