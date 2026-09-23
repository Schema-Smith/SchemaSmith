// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

// Declared scheduled events, drop-by-absence (MySQL/MariaDB, 2.6.0 F4).
//
// WHAT THIS FIXTURE IS FOR, stated plainly: it pins the PROCEDURE CONTRACT that the fix for the
// last-declared-event defect depends on -- that SchemaSmith_EventQuench, handed a declared set of "[]"
// with by-absence removal on, drops the events it owns and leaves everything else alone.
//
// These tests PASSED BEFORE that fix and still pass after it. They are not the regression test, and
// should not be mistaken for one. The defect was C# control flow -- the step gate never invoked the
// procedure when nothing was declared, so this contract was correct all along and simply unreachable.
// The regression test is DatabaseQuenchEventStepGateTests in the unit suite, which is where a
// control-flow defect belongs.
//
// What earns these their place is that nothing else asserts this contract. The fix routes a case here
// that had never arrived before, and if the procedure mishandled an empty declared set -- by dropping
// nothing, or by dropping everything including hand-made events -- the fix would turn a silent no-op
// into something considerably worse.
//
// Each test owns a UNIQUE product and event name so it is scoped to its own objects under parallel
// execution.
[Category("Integration")]
public abstract class EventQuench_DropByAbsenceSharedTests : BaseTableQuenchTests
{
    [Test]
    public void EventQuench_RemovingTheLastDeclaredEvent_StillDropsIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"EvtLastProduct_{uid}";
        var evt = $"evt_last_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // SchemaSmith creates the event itself, so ownership is unambiguous -- this is roadmap case C/E,
            // not the hand-created case that is SUPPOSED to survive.
            RunEventQuench(cmd, OneEvent(evt), product, dropRemoved: true);
            Assert.That(LiveEventExists(cmd, evt), Is.True, "Setup: the declared event must deploy.");

            // The package now declares NO events at all -- the empty Events/ folder.
            RunEventQuench(cmd, "[]", product, dropRemoved: true);

            Assert.That(LiveEventExists(cmd, evt), Is.False,
                "removing the LAST declared event must drop it -- an empty Events/ folder means "
                + "'declare none', not 'skip the comparison'. Leaving it deployed leaves a live "
                + "scheduled job running after the package that owned it says it is gone");
        }
        finally
        {
            DropEvent(cmd, evt);
        }
        conn.Close();
    }

    // The discriminator from the roadmap's controlled sequence: case D (one other event still declared)
    // always worked. Keeping it here means a fix that somehow only repaired the empty case, or only the
    // non-empty one, cannot pass.
    [Test]
    public void EventQuench_RemovingOneOfTwoDeclaredEvents_StillDropsOnlyThatOne()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"EvtTwoProduct_{uid}";
        var goes = $"evt_goes_{uid}";
        var stays = $"evt_stays_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunEventQuench(cmd, TwoEvents(goes, stays), product, dropRemoved: true);
            RunEventQuench(cmd, OneEvent(stays), product, dropRemoved: true);

            Assert.Multiple(() =>
            {
                Assert.That(LiveEventExists(cmd, goes), Is.False, "the removed event drops");
                Assert.That(LiveEventExists(cmd, stays), Is.True, "the still-declared one is untouched");
            });
        }
        finally
        {
            DropEvent(cmd, goes);
            DropEvent(cmd, stays);
        }
        conn.Close();
    }

    // Ownership was always correct and must not regress: removal reaches only events SchemaSmith created.
    [Test]
    public void EventQuench_EmptyingTheFolder_LeavesAHandMadeEventAlone()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"EvtOwnProduct_{uid}";
        var owned = $"evt_owned_{uid}";
        var handmade = $"evt_handmade_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            CreateEventByHand(cmd, handmade);
            RunEventQuench(cmd, OneEvent(owned), product, dropRemoved: true);
            RunEventQuench(cmd, "[]", product, dropRemoved: true);

            Assert.Multiple(() =>
            {
                Assert.That(LiveEventExists(cmd, owned), Is.False,
                    "the SchemaSmith-created event drops");
                Assert.That(LiveEventExists(cmd, handmade), Is.True,
                    "and ownership still holds -- an event made by hand is never touched. Reaching the "
                    + "by-absence pass on an empty declaration must not turn it into a blanket drop");
            });
        }
        finally
        {
            DropEvent(cmd, owned);
            DropEvent(cmd, handmade);
        }
        conn.Close();
    }

    // Roadmap case A: with the flag off, by-absence removal stays off. The fix must not make the empty
    // folder an unconditional drop.
    [Test]
    public void EventQuench_EmptyingTheFolder_WithTheFlagOff_LeavesTheEventDeployed()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"EvtFlagOffProduct_{uid}";
        var evt = $"evt_flagoff_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunEventQuench(cmd, OneEvent(evt), product, dropRemoved: true);
            RunEventQuench(cmd, "[]", product, dropRemoved: false);

            Assert.That(LiveEventExists(cmd, evt), Is.True,
                "with DropEventsRemovedFromProduct off, by-absence removal stays off");
        }
        finally
        {
            DropEvent(cmd, evt);
        }
        conn.Close();
    }

    [Test]
    public void EventQuench_ADisableOnSlaveEvent_IsNotReAppliedOnEveryDeploy()
    {
        // MySQL 8.4 renamed the catalog status SLAVESIDE_DISABLED to REPLICA_SIDE_DISABLED. Extraction
        // was taught the new spelling AND so was the deploy-side comparison, but only the extraction
        // half had a test. Unmatched, the comparison re-applied a DISABLE ON SLAVE event on EVERY
        // deploy, forever, at exit 0 with nothing in the log to say why -- the silent per-deploy churn
        // this release fixes in several places, and the one where the engine version that triggers it
        // reaches CI on a merge leg only.
        //
        // Deploy-twice is the shape that catches it: the second run must have nothing to do. On a server
        // that still reports the OLD spelling this passes for the other branch of the same CASE, which
        // is the point -- the event must round-trip on both.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"EvtSlaveProduct_{uid}";
        var evt = $"evt_slave_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var first = RunEventQuench(cmd, SlaveDisabledEvent(evt), product, dropRemoved: true);
            Assert.That(LiveEventExists(cmd, evt), Is.True, "Setup: the declared event must deploy.");
            Assert.That(first, Is.Not.Empty,
                "Setup: the first deploy must actually do something, or the second proving nothing "
                + "would prove nothing either.");

            var second = RunEventQuench(cmd, SlaveDisabledEvent(evt), product, dropRemoved: true);

            // Scoped to EVENT DDL rather than "no statements at all": every run also re-asserts the
            // ownership row with an INSERT ... WHERE NOT EXISTS, which is idempotent bookkeeping and
            // writes nothing the second time. Churn of the EVENT is what the status rename caused and
            // what this has to catch.
            var eventDdl = second
                .Where(s => s.Contains("EVENT", StringComparison.OrdinalIgnoreCase)
                            && !s.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.That(eventDdl, Is.Empty,
                "re-deploying an unchanged DISABLE ON SLAVE event must generate no event DDL. A "
                + "comparison that cannot read the status back re-applies it on every single deploy, "
                + "silently, and the deploy still reports success: " + string.Join(" | ", eventDdl));
        }
        finally
        {
            DropEvent(cmd, evt);
            conn.Close();
        }
    }

    // ---- fixtures -------------------------------------------------------------

    // Keys are the ones SchemaSmith_EventQuench actually reads (see its _SchemaSmith_Events INSERT):
    // Name, Definition, ScheduleType, Interval, ExecuteAt, Starts, Ends, Status, Preserve, Comment.
    // DISABLE keeps the event from firing during the test run.
    private static string SlaveDisabledEvent(string name) => $$"""
[
  {
    "Name": "{{name}}",
    "ScheduleType": "EVERY",
    "Interval": "1 DAY",
    "Status": "DISABLE ON SLAVE",
    "Definition": "SET @ss_test = 1"
  }
]
""";

    private static string OneEvent(string name) => $$"""
[
  {
    "Name": "{{name}}",
    "ScheduleType": "EVERY",
    "Interval": "1 DAY",
    "Status": "DISABLE",
    "Definition": "SET @ss_test = 1"
  }
]
""";

    private static string TwoEvents(string first, string second) => $$"""
[
  {
    "Name": "{{first}}",
    "ScheduleType": "EVERY",
    "Interval": "1 DAY",
    "Status": "DISABLE",
    "Definition": "SET @ss_test = 1"
  },
  {
    "Name": "{{second}}",
    "ScheduleType": "EVERY",
    "Interval": "1 DAY",
    "Status": "DISABLE",
    "Definition": "SET @ss_test = 2"
  }
]
""";

    // ---- drivers and live-state readers ---------------------------------------

    /// <summary>
    /// Drives the same path DatabaseQuench.QuenchEvents drives. EventQuench does not execute event DDL
    /// itself -- MySQL cannot PREPARE CREATE EVENT or DROP EVENT (error 1295) -- so it RETURNS an ordered
    /// statement list that the caller runs. The whole list is read BEFORE any of it executes, because the
    /// reader holds the connection the statements then run on.
    /// </summary>
    /// <summary>Runs the quench and returns the statements it generated, so a caller can assert that a
    /// second identical run generates NONE -- which is what deploy-time idempotency means here.</summary>
    private List<string> RunEventQuench(IDbCommand cmd, string eventsJson, string productName, bool dropRemoved)
    {
        var escaped = eventsJson.Replace("'", "''");
        cmd.CommandText =
            $"CALL SchemaSmith_EventQuench('{productName}', '{_mainDb}', '{escaped}', 0, "
            + $"{(dropRemoved ? 1 : 0)}, 'Main')";

        var statements = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                if (!reader.IsDBNull(0)) statements.Add(reader.GetString(0));
        }

        foreach (var statement in statements)
        {
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }

        return statements;
    }

    private void CreateEventByHand(IDbCommand cmd, string name)
    {
        cmd.CommandText =
            $"CREATE EVENT `{_mainDb}`.`{name}` ON SCHEDULE EVERY 1 DAY "
            + "ON COMPLETION NOT PRESERVE DISABLE DO SET @ss_test = 99;";
        cmd.ExecuteNonQuery();
    }

    private void DropEvent(IDbCommand cmd, string name)
    {
        cmd.CommandText = $"DROP EVENT IF EXISTS `{_mainDb}`.`{name}`;";
        cmd.ExecuteNonQuery();
    }

    private bool LiveEventExists(IDbCommand cmd, string name)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.EVENTS
 WHERE EVENT_SCHEMA = '{_mainDb}' AND EVENT_NAME = '{name}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }
}
