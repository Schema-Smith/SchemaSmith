// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// PostgreSQL enum types, promoted from scripted to declarative (F5).
/// <para><b>This replaces a silent no-op, which is the worst kind of bug.</b> As a scripted object an enum
/// is created by a guarded <c>CREATE TYPE</c>. Once the type exists that guard skips — so editing the
/// value list in the <c>.sql</c> file does nothing, forever, and the deploy reports success.
/// <see cref="TheScriptedFormsSilentNoOp_IsWhatThisReplaces"/> pins that behaviour so the reason this
/// exists cannot be forgotten.</para>
/// <para><b>What can converge is the engine's limit, not a design choice.</b> PostgreSQL can ADD a value
/// and place it, but cannot REMOVE or REORDER one without recreating the type — which means dropping
/// every column that uses it. A value removed from the package is therefore reported, never dropped.</para>
/// <para>Order is part of the type: PostgreSQL sorts and compares enum values by declared position, so a
/// value the package inserts in the middle is added <i>in the middle</i>, not appended.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class EnumTypeTests
{
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _props = new();
    private string _db = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["PostgreSQL:Server"] ?? "127.0.0.1";
        _user = config["PostgreSQL:User"];
        _password = config["PostgreSQL:Password"];
        _port = config["PostgreSQL:Port"];
        _props = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");

        _db = $"ss_enum_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
        using var maint = Open("postgres");
        Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        Exec(maint, $"CREATE DATABASE \"{_db}\"");

        using var c = Open(_db);
        using var cmd = c.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var maint = Open("postgres");
            Exec(maint, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_db}' AND pid <> pg_backend_pid()");
            Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        }
        catch (DbException) { /* teardown must not mask an assertion */ }
    }

    private IDbConnection Open(string database)
    {
        var c = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(ConnectionString.Build(Platform.PostgreSQL, _server, database, _user, _password, _port, _props));
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

    private void OnDb(Action<IDbCommand> act)
    {
        using var c = Open(_db);
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    private static string Package(string name, params string[] values) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + name + "\", \"Values\": ["
        + string.Join(", ", Array.ConvertAll(values, v => "\"" + v + "\"")) + "] }]";

    private static void Deploy(IDbCommand cmd, string json, bool whatIf = false)
    {
        cmd.CommandText = "CALL \"SchemaSmith\".\"EnumTypeQuench\"('EnumTest', $ss$" + json + "$ss$, "
                          + (whatIf ? "true" : "false") + ")";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Values in declared order — which is the only order that means anything for an enum.</summary>
    private static string Values(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT COALESCE(STRING_AGG(e.enumlabel, ',' ORDER BY e.enumsortorder), '') "
                          + "FROM pg_enum e JOIN pg_type t ON t.oid = e.enumtypid WHERE t.typname = '" + name + "'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    [Test]
    public void TheScriptedFormsSilentNoOp_IsWhatThisReplaces()
    {
        // Not a test of SchemaSmith -- a test of the ENGINE behaviour that made the scripted form
        // useless, pinned so the reason this feature exists cannot be lost. A guarded CREATE TYPE skips
        // once the type exists, so the third value never arrives and nothing reports it.
        OnDb(cmd =>
        {
            Exec2(cmd, "DROP TYPE IF EXISTS scripted_t CASCADE");
            Exec2(cmd, "CREATE TYPE scripted_t AS ENUM ('new','open')");
            Exec2(cmd, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname='scripted_t') "
                       + "THEN CREATE TYPE scripted_t AS ENUM ('new','open','closed'); END IF; END $$");

            Assert.That(Values(cmd, "scripted_t"), Is.EqualTo("new,open"),
                "the guarded re-create silently does nothing -- this is the failure the declarative form fixes");
        });
    }

    [Test]
    public void ADeclaredEnumType_IsCreatedWithItsValuesInOrder()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("colour_t", "red", "green", "blue"));

            Assert.That(Values(cmd, "colour_t"), Is.EqualTo("red,green,blue"));
        });
    }

    [Test]
    public void AnAddedValue_IsApplied()
    {
        // The case the scripted form could never do.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("state_t", "new", "open"));
            Assert.That(Values(cmd, "state_t"), Is.EqualTo("new,open"), "precondition");

            Deploy(cmd, Package("state_t", "new", "open", "closed"));

            Assert.That(Values(cmd, "state_t"), Is.EqualTo("new,open,closed"));
        });
    }

    [Test]
    public void AValueAddedInTheMiddle_LandsInTheMiddle()
    {
        // Order is part of an enum's meaning: PostgreSQL sorts and compares by declared position, so
        // appending a value the package placed in the middle would give the type different semantics
        // from the one declared -- and no error would say so.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("stage_t", "start", "finish"));

            Deploy(cmd, Package("stage_t", "start", "middle", "finish"));

            Assert.That(Values(cmd, "stage_t"), Is.EqualTo("start,middle,finish"),
                "a value declared in the middle must not be appended to the end");
        });
    }

    [Test]
    public void SeveralValuesAddedAtOnce_AllLandInOrder()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("multi_t", "a", "d"));

            Deploy(cmd, Package("multi_t", "a", "b", "c", "d", "e"));

            Assert.That(Values(cmd, "multi_t"), Is.EqualTo("a,b,c,d,e"));
        });
    }

    [Test]
    public void RedeployingAnUnchangedType_DoesNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("idem_t", "one", "two"));
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%idem_t%'");

            Deploy(cmd, Package("idem_t", "one", "two"));

            cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%idem_t%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "an unchanged enum must produce no change at all");
        });
    }

    [Test]
    public void AValueRemovedFromThePackage_IsReportedAndLeftAlone()
    {
        // PostgreSQL cannot remove an enum value without recreating the type, which would mean dropping
        // every column using it. Doing that because a string left a file would be catastrophic, so it is
        // reported by name and the value stays.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("keep_t", "a", "b", "c"));

            Deploy(cmd, Package("keep_t", "a", "c"));

            Assert.Multiple(() =>
            {
                Assert.That(Values(cmd, "keep_t"), Is.EqualTo("a,b,c"), "the value must NOT be removed");
                cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" "
                                  + "WHERE \"ActionType\" = 'wouldDrop' AND \"ObjectName\" LIKE '%keep_t.b'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "and the manifest has to name it, or the divergence is invisible");
            });
        });
    }

    [Test]
    public void AnEnumTypeInUseByAColumn_StillConverges()
    {
        // The realistic case: the type is not free-floating, it types a column. Adding a value must work
        // with data present, which is exactly why ADD VALUE is used rather than a recreate.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("inuse_t", "draft"));
            Exec2(cmd, "DROP TABLE IF EXISTS doc");
            Exec2(cmd, "CREATE TABLE doc (id int, state inuse_t)");
            Exec2(cmd, "INSERT INTO doc VALUES (1, 'draft')");

            Deploy(cmd, Package("inuse_t", "draft", "published"));

            Assert.Multiple(() =>
            {
                Assert.That(Values(cmd, "inuse_t"), Is.EqualTo("draft,published"));
                cmd.CommandText = "SELECT COUNT(*) FROM doc WHERE state = 'draft'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "and the existing row survives");
            });
        });
    }

    [Test]
    public void ItRoundTripsThroughExtraction_WithOrderIntact()
    {
        // Order is the whole point. PostgreSQL sorts and compares enum values by declared position, so
        // an extraction that emitted them in any other order would produce a package that redeploys a
        // DIFFERENT type from the one it read -- and nothing would report it.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("round_t", "zulu", "alpha", "mike"));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateEnumTypeJSON\"('public', 'round_t')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("zulu"), json);
                Assert.That(json.IndexOf("zulu", StringComparison.Ordinal),
                    Is.LessThan(json.IndexOf("alpha", StringComparison.Ordinal)),
                    "values must extract in DECLARED order, not alphabetically." + json);
                Assert.That(json.IndexOf("alpha", StringComparison.Ordinal),
                    Is.LessThan(json.IndexOf("mike", StringComparison.Ordinal)), json);
            });
        });
    }

    [Test]
    public void WhatIf_ChangesNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("whatif_t", "x"), whatIf: true);

            cmd.CommandText = "SELECT COUNT(*) FROM pg_type WHERE typname = 'whatif_t'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "a preview with side effects is the worst of both");
        });
    }

    private static void Exec2(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Comment(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT OBJ_DESCRIPTION(t.oid, 'pg_type') FROM pg_type t WHERE t.typname = '" + name + "'";
        return cmd.ExecuteScalar() as string;
    }

    [Test]
    public void ADeclaredDescription_IsSet_ChangedInPlace_LeftAloneWhenOmitted_AndRemovedByAnEmptyString()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"mood_t\", \"Values\": [\"sad\", \"happy\"], \"Description\": \"how it feels\" }]");
            Assert.That(Comment(cmd, "mood_t"), Is.EqualTo("how it feels"), "set on create");

            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"mood_t\", \"Values\": [\"sad\", \"happy\"], \"Description\": \"how it feels today\" }]");
            Assert.That(Comment(cmd, "mood_t"), Is.EqualTo("how it feels today"), "a change converges in place");

            Deploy(cmd, Package("mood_t", "sad", "happy"));
            Assert.That(Comment(cmd, "mood_t"), Is.EqualTo("how it feels today"), "omitted means the comment is not managed, so the database's stays");

            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"mood_t\", \"Values\": [\"sad\", \"happy\"], \"Description\": \"\" }]");
            Assert.That(Comment(cmd, "mood_t"), Is.Null, "an empty string is the request to remove it");
        });
    }
}
