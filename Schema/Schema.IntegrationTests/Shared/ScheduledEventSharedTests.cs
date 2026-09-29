// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// MySQL/MariaDB scheduled events, promoted from a scripted-object folder to a MANAGED type (F4).
/// <para><b>What changes.</b> As a scripted object an event was re-run on every deploy (DROP then CREATE),
/// never compared, and never removed when it left the package — so a retired event kept firing until
/// someone dropped it by hand. Declared, it converges and can be dropped by absence.</para>
/// <para><b>Additive, deliberately.</b> The audit framed this as "removal-and-replace", which would break
/// every package that already has an <c>Events/</c> folder. Instead the same folder now holds
/// <c>.json</c> (declarative, managed) alongside <c>.sql</c> (scripted, exactly as before), so no existing
/// package changes behaviour and migration is per-event and optional.</para>
/// <para><b>The hard part is "unchanged".</b> ALTER EVENT cannot change every attribute and CREATE OR
/// REPLACE EVENT is MariaDB-only, so converging means DROP + CREATE. A comparison that reported a
/// difference when there was none would not merely churn — it would <i>reset the event's schedule on
/// every deploy</i>, which for a nightly job means pushing it past its window forever. The catalog and the
/// DDL disagree on spelling in four separate ways, and each one is a chance to get that wrong.</para>
/// </summary>
public abstract class ScheduledEventSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection = null!;
    private const string EventName = "ss_event_test";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        Exec($"DROP EVENT IF EXISTS `{MainDb}`.`{EventName}`");
        Exec($"DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectType = 'EVENT' AND ObjectName = '{EventName}'");
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{EventName}%'");
    }

    [TearDown]
    public void TearDown() => Exec($"DROP EVENT IF EXISTS `{MainDb}`.`{EventName}`");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private long Scalar(string sql) => Convert.ToInt64(ScalarStr(sql) ?? "0");

    private static string EventJson(string interval = "1 DAY", string status = "ENABLE",
                                    bool preserve = false, string comment = null, string body = "SET @ss_noop = 1")
        => "[{ \"Name\": \"" + EventName + "\", \"Definition\": \"" + body + "\","
           + " \"ScheduleType\": \"EVERY\", \"Interval\": \"" + interval + "\","
           + " \"Status\": \"" + status + "\", \"Preserve\": " + (preserve ? "true" : "false")
           + (comment == null ? "" : ", \"Comment\": \"" + comment + "\"") + " }]";

    /// <summary>
    /// Mirrors what DatabaseQuench.QuenchEvents does: the procedure DECIDES and returns an ordered list
    /// of statements, and the caller executes them. MySQL cannot PREPARE event DDL (1295), so the
    /// procedure physically cannot run it itself -- see the comment on SchemaSmith_EventQuench.
    /// </summary>
    private void Deploy(string eventsJson, bool dropRemoved = false, bool whatIf = false)
    {
        var json = (eventsJson ?? "[]").Replace("'", "''");
        var statements = new System.Collections.Generic.List<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = $"CALL SchemaSmith_EventQuench('EventProduct', '{MainDb}', '{json}', "
                              + $"{(whatIf ? 1 : 0)}, {(dropRemoved ? 1 : 0)}, 'Main')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0)) statements.Add(reader.GetString(0));
        }
        foreach (var s in statements) Exec(s);
    }

    private long EventCount() => Scalar(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.EVENTS WHERE EVENT_SCHEMA = '{MainDb}' AND EVENT_NAME = '{EventName}'");

    private string EventField(string col) => ScalarStr(
        $"SELECT {col} FROM INFORMATION_SCHEMA.EVENTS WHERE EVENT_SCHEMA = '{MainDb}' AND EVENT_NAME = '{EventName}'");

    private long AuditCount(string action) => Scalar(
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType = '{action}' AND ObjectName LIKE '%{EventName}%'");

    [Test]
    public void ADeclaredEvent_IsCreated()
    {
        Deploy(EventJson());

        Assert.Multiple(() =>
        {
            Assert.That(EventCount(), Is.EqualTo(1));
            Assert.That(EventField("STATUS"), Is.EqualTo("ENABLED"));
        });
    }

    // ---- ScheduleType / Status spelling must not be decided by the database's collation ----
    //
    // The two values land in temp-table columns that carried no collation, so they inherited the TARGET
    // DATABASE's. Under a NO PAD collation (MySQL 8.0's utf8mb4_0900_ai_ci) 'AT ' <> 'AT', so a one-time event
    // deployed as EVERY 1 DAY; under PAD SPACE (MariaDB's uca1400) it deployed one-time. For Status it was
    // worse: 'DISABLE ' fell through to ELSE 'ENABLE', so an event authored as OFF deployed ON. The schema
    // already rejects every one of these spellings, so the deploy was guessing on invalid input -- and guessing
    // differently per database. These run on both engines; the MySQL leg (NO PAD) is the one that reddens.

    private static string EventJsonWith(string scheduleType, string status = "ENABLE")
        => "[{ \"Name\": \"" + EventName + "\", \"Definition\": \"SET @ss_noop = 1\","
           + " \"ScheduleType\": \"" + scheduleType + "\", \"ExecuteAt\": \"2037-06-01 00:00:00\","
           + " \"Interval\": \"1 DAY\", \"Status\": \"" + status + "\", \"Preserve\": true }]";

    [TestCase("AT ", TestName = "ScheduleType_WithTrailingSpace_DeploysOneTime")]
    [TestCase(" AT", TestName = "ScheduleType_WithLeadingSpace_DeploysOneTime")]
    [TestCase("at", TestName = "ScheduleType_Lowercase_DeploysOneTime")]
    public void ScheduleTypeSpelling_DeploysTheSameOnEveryCollation(string spelling)
    {
        Deploy(EventJsonWith(spelling));

        Assert.That(EventField("EVENT_TYPE"), Is.EqualTo("ONE TIME"),
            $"ScheduleType '{spelling}' must deploy as a one-time event on every database collation; a NO PAD "
            + "collation compared it unequal to 'AT' and made it recurring");
    }

    [TestCase("DISABLE ", TestName = "Status_WithTrailingSpace_DeploysDisabled")]
    [TestCase("disable", TestName = "Status_Lowercase_DeploysDisabled")]
    public void StatusSpelling_DeploysTheSameOnEveryCollation(string spelling)
    {
        Deploy(EventJsonWith("EVERY", spelling));

        Assert.That(EventField("STATUS"), Is.EqualTo("DISABLED"),
            $"Status '{spelling}' was authored as DISABLE; falling through to ENABLE turns on an event the "
            + "package switched off");
    }

    // Anything still outside the vocabulary after trimming and upper-casing is REFUSED. Guessing meant EVERY
    // and ENABLE -- the two directions that make an event run -- and which way it went depended on whether
    // the collation happened to be accent- or width-insensitive.
    [TestCase("àt", "ENABLE", TestName = "ScheduleType_Accented_IsRefused")]
    [TestCase("ＡＴ", "ENABLE", TestName = "ScheduleType_FullWidth_IsRefused")]
    [TestCase("ONCE", "ENABLE", TestName = "ScheduleType_Unknown_IsRefused")]
    [TestCase("EVERY", "OFF", TestName = "Status_Unknown_IsRefused")]
    public void UnrecognisedValue_IsRefused_RatherThanGuessed(string scheduleType, string status)
    {
        Assert.Catch<Exception>(() => Deploy(EventJsonWith(scheduleType, status)),
            "a value the schema rejects must stop the deploy, not be silently read as EVERY / ENABLE");
        Assert.That(EventCount(), Is.Zero, "nothing may be created from a refused definition");
    }

    // Normalising on the way in must not make the NEXT deploy think the event changed -- that would drop and
    // recreate it every run, resetting its schedule.
    [Test]
    public void ANormalisedSpelling_RedeploysAsUnchanged()
    {
        Deploy(EventJsonWith("at ", "disable "));
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{EventName}%'");

        Deploy(EventJsonWith("at ", "disable "));

        Assert.That(AuditCount("created"), Is.Zero, "a normalised spelling must redeploy as unchanged");
    }


    [Test]
    public void RedeployingAnUnchangedEvent_DoesNothing()
    {
        // THE test. Converging means DROP + CREATE, so a comparison that mis-reports "changed" resets the
        // event's schedule every deploy -- a nightly job pushed past its window, forever. Four separate
        // spelling mismatches between the catalog and the DDL make this easy to get wrong.
        Deploy(EventJson());
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{EventName}%'");

        Deploy(EventJson());

        Assert.That(AuditCount("created"), Is.Zero,
            "an unchanged event must not be dropped and recreated");
    }

    [Test]
    public void ChangingTheInterval_Converges()
    {
        Deploy(EventJson(interval: "1 DAY"));
        Assert.That(EventField("INTERVAL_VALUE"), Is.EqualTo("1"), "precondition");

        Deploy(EventJson(interval: "6 HOUR"));

        Assert.Multiple(() =>
        {
            Assert.That(EventField("INTERVAL_VALUE"), Is.EqualTo("6"));
            Assert.That(EventField("INTERVAL_FIELD"), Is.EqualTo("HOUR"));
        });
    }

    [Test]
    public void IntervalCasingAndSpacing_IsNotAChange()
    {
        // "1 DAY" and "1  day" are the same schedule. Rebuilding an event over its capitalisation would
        // be absurd, and would reset the schedule to boot.
        Deploy(EventJson(interval: "1 DAY"));
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{EventName}%'");

        Deploy(EventJson(interval: "1  day"));

        Assert.That(AuditCount("created"), Is.Zero, EventField("INTERVAL_VALUE") + " " + EventField("INTERVAL_FIELD"));
    }

    [Test]
    public void ChangingStatus_Converges()
    {
        // Catalog says ENABLED/DISABLED; the package says ENABLE/DISABLE. Comparing either side raw
        // reports a difference on every deploy.
        Deploy(EventJson(status: "ENABLE"));

        Deploy(EventJson(status: "DISABLE"));

        Assert.That(EventField("STATUS"), Is.EqualTo("DISABLED"));
    }

    [Test]
    public void ChangingPreserve_Converges()
    {
        // Catalog says 'PRESERVE' / 'NOT PRESERVE'; the package says a bool.
        Deploy(EventJson(preserve: false));
        Assert.That(EventField("ON_COMPLETION"), Is.EqualTo("NOT PRESERVE"), "precondition");

        Deploy(EventJson(preserve: true));

        Assert.That(EventField("ON_COMPLETION"), Is.EqualTo("PRESERVE"));
    }

    [Test]
    public void ChangingTheComment_Converges()
    {
        Deploy(EventJson(comment: "first"));

        Deploy(EventJson(comment: "second"));

        Assert.That(EventField("EVENT_COMMENT"), Is.EqualTo("second"));
    }

    [Test]
    public void AnEventRemovedFromThePackage_IsNotDroppedByDefault()
    {
        // Events were scripted objects that were NEVER removed by absence. Turning that on by default
        // would start deleting events on the first deploy after upgrading, which is why the flag defaults
        // off and why this test exists rather than only its opposite.
        Deploy(EventJson());
        Assert.That(EventCount(), Is.EqualTo(1), "precondition");

        Deploy("[]");

        Assert.That(EventCount(), Is.EqualTo(1), "absence alone must not drop an event");
    }

    [Test]
    public void AnEventRemovedFromThePackage_IsDroppedWhenAsked()
    {
        Deploy(EventJson());

        Deploy("[]", dropRemoved: true);

        Assert.Multiple(() =>
        {
            Assert.That(EventCount(), Is.Zero);
            Assert.That(AuditCount("dropped"), Is.EqualTo(1));
        });
    }

    [Test]
    public void AnUnownedEvent_IsNeverDropped()
    {
        // The scoping that makes drop-by-absence safe. An event created by hand -- or by a scripted
        // Events/ .sql file, which is still fully supported -- has no ownership row, so it is invisible
        // to drop-by-absence. Without this, enabling the flag would delete every event on the database
        // the package happens not to mention.
        Exec($"CREATE EVENT `{MainDb}`.`{EventName}` ON SCHEDULE EVERY 1 DAY DO SET @ss_noop = 1");

        Deploy("[]", dropRemoved: true);

        Assert.That(EventCount(), Is.EqualTo(1),
            "an event SchemaSmith does not own must survive drop-by-absence");
    }

    [Test]
    public void ItRoundTripsThroughExtraction()
    {
        // Extraction translates the catalog vocabulary back to the DDL spelling an author writes. Four
        // separate translations, each of which would otherwise round-trip into something that no longer
        // matches and rebuilds the event on the next deploy.
        Deploy(EventJson(interval: "6 HOUR", status: "DISABLE", preserve: true, comment: "nightly"));

        var json = ScalarStr($"CALL SchemaSmith_GenerateEventJSON('{MainDb}', '{EventName}')") ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Status\": \"DISABLE\"").Or.Contain("\"Status\":\"DISABLE\""),
                "the catalog says DISABLED; the package must say DISABLE. " + json);
            Assert.That(json, Does.Contain("6 HOUR"), "interval is two catalog columns, one package string. " + json);
            Assert.That(json, Does.Contain("nightly"), json);
            Assert.That(json, Does.Contain("true"), "ON_COMPLETION PRESERVE must come back as a bool. " + json);
        });
    }

    [Test]
    public void ExtractionDoesNotCaptureStarts()
    {
        // THE one that matters most, and it is invisible unless you look for it. The server MATERIALISES
        // STARTS to the creation time when it was not specified. Capturing it would pin the event to
        // whenever it happened to be created -- and every later deploy would then see drift, drop the
        // event and recreate it, RESETTING ITS SCHEDULE each time. A nightly job would walk forward on
        // every deploy and nothing would look wrong.
        Deploy(EventJson());

        var json = ScalarStr($"CALL SchemaSmith_GenerateEventJSON('{MainDb}', '{EventName}')") ?? "";

        Assert.That(json, Does.Not.Contain("Starts"),
            "the server assigns STARTS itself; capturing it makes every future deploy see drift." + json);
    }

    [Test]
    public void AnExtractedEventRedeploysAsUnchanged()
    {
        // The round trip that actually proves it: extract what was deployed, feed it straight back, and
        // nothing should happen. This is the test that would have caught the STARTS problem on its own.
        Deploy(EventJson(interval: "6 HOUR", status: "DISABLE", preserve: true, comment: "nightly"));
        var json = ScalarStr($"CALL SchemaSmith_GenerateEventJSON('{MainDb}', '{EventName}')") ?? "";
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{EventName}%'");

        Deploy("[" + json + "]");

        Assert.That(AuditCount("created"), Is.Zero,
            "an extracted event fed straight back must read as unchanged." + json);
    }

    [Test]
    public void WhatIf_ReportsWithoutChanging()
    {
        Deploy(EventJson(), whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(EventCount(), Is.Zero, "a preview with side effects is the worst of both");
            Assert.That(AuditCount("wouldModify"), Is.EqualTo(1), "and it has to say what it would do");
        });
    }
}
