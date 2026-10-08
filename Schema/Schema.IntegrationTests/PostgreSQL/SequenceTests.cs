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
/// PostgreSQL sequences, promoted from scripted to declarative (F5).
/// <para>Unlike enum types every attribute is genuinely alterable in place, so this converges properly
/// and has nothing to refuse.</para>
/// <para><b>The current value is never managed, and that is the line that matters.</b> A sequence's
/// position is data — which numbers have already been handed out. A package that carried it would reset
/// a live sequence on deploy and re-issue keys already in use. <c>Start</c> is the declared starting
/// point and applies only at creation; nothing here ever emits <c>RESTART</c>.</para>
/// <para><b>Engine-owned sequences are excluded</b> — see #409. A <c>serial</c> column's sequence is
/// recorded with <c>pg_depend.deptype = 'a'</c> and an <c>IDENTITY</c> column's with <c>'i'</c>; the old
/// filter caught only the latter, so every <c>serial</c> sequence was extracted as standalone.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class SequenceTests
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

        _db = $"ss_seq_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
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


    private static string Package(string name, string extra = "") =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + name + "\"" + extra + " }]";

    private static void Deploy(IDbCommand cmd, string json, bool whatIf = false)
    {
        cmd.CommandText = "CALL \"SchemaSmith\".\"SequenceQuench\"('SeqTest', $ss$" + json + "$ss$, "
                          + (whatIf ? "true" : "false") + ")";
        cmd.ExecuteNonQuery();
    }

    private static string Attr(IDbCommand cmd, string name, string column)
    {
        cmd.CommandText = "SELECT q." + column + "::text FROM pg_sequence q JOIN pg_class c ON c.oid = q.seqrelid "
                          + "WHERE c.relname = '" + name + "'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    [Test]
    public void ADeclaredSequence_IsCreatedWithItsAttributes()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("seq_new", ", \"Increment\": 5, \"MinValue\": 10, \"MaxValue\": 500, \"Cache\": 3, \"Cycle\": true"));

            Assert.Multiple(() =>
            {
                Assert.That(Attr(cmd, "seq_new", "seqincrement"), Is.EqualTo("5"));
                Assert.That(Attr(cmd, "seq_new", "seqmin"), Is.EqualTo("10"));
                Assert.That(Attr(cmd, "seq_new", "seqmax"), Is.EqualTo("500"));
                Assert.That(Attr(cmd, "seq_new", "seqcache"), Is.EqualTo("3"));
                Assert.That(Attr(cmd, "seq_new", "seqcycle"), Is.EqualTo("true"));
            });
        });
    }

    // UNLOGGED sequences (PostgreSQL 15+): created as declared, extracted, converged both ways, and left alone by a
    // package that does not say. Below 15 the clause is a downgrade under the unsupported-feature policy.
    [Test]
    public void Persistence_IsCreatedExtractedAndConverged()
    {
        OnDb(cmd =>
        {
            cmd.CommandText = "SELECT current_setting('server_version_num')::int";
            if (Convert.ToInt32(cmd.ExecuteScalar()) < 150000)
                Assert.Ignore("UNLOGGED sequences need PostgreSQL 15; below it the downgrade path applies.");

            string Persistence() { cmd.CommandText = "SELECT relpersistence::text FROM pg_class WHERE relname = 'seq_unlogged'"; return Convert.ToString(cmd.ExecuteScalar()); }

            Deploy(cmd, Package("seq_unlogged", ", \"PersistenceType\": \"Unlogged\""));
            Assert.That(Persistence(), Is.EqualTo("u"), "the sequence must be created unlogged");
            cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" = 'public.seq_unlogged' AND \"ActionType\" = 'modified'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0),
                "it must be created unlogged, not created and then altered -- the summary would report a change that was not one");

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateSequenceJSON\"('public', 'seq_unlogged')";
            Assert.That(Convert.ToString(cmd.ExecuteScalar()), Does.Contain("\"PersistenceType\": \"Unlogged\""),
                "extraction must carry it");

            Deploy(cmd, Package("seq_unlogged"));
            Assert.That(Persistence(), Is.EqualTo("u"), "a package that does not declare it must leave it alone");

            Deploy(cmd, Package("seq_unlogged", ", \"PersistenceType\": \"Logged\""));
            Assert.That(Persistence(), Is.EqualTo("p"), "a declared change must converge");
        });
    }

    [Test]
    public void ChangedAttributes_Converge()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("seq_conv", ", \"Increment\": 1, \"MaxValue\": 100, \"Cycle\": false"));

            Deploy(cmd, Package("seq_conv", ", \"Increment\": 7, \"MaxValue\": 900, \"Cycle\": true"));

            Assert.Multiple(() =>
            {
                Assert.That(Attr(cmd, "seq_conv", "seqincrement"), Is.EqualTo("7"));
                Assert.That(Attr(cmd, "seq_conv", "seqmax"), Is.EqualTo("900"));
                Assert.That(Attr(cmd, "seq_conv", "seqcycle"), Is.EqualTo("true"));
            });
        });
    }

    [Test]
    public void RedeployingAnUnchangedSequence_DoesNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("seq_idem", ", \"Increment\": 2"));
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%seq_idem%'");

            Deploy(cmd, Package("seq_idem", ", \"Increment\": 2"));

            cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%seq_idem%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "an unchanged sequence must emit no ALTER at all");
        });
    }

    [Test]
    public void TheCurrentValueIsNeverReset()
    {
        // THE test. A sequence's position records which numbers have already been handed out. If a deploy
        // reset it, the next insert would re-issue keys that are already in use -- a silent duplicate-key
        // generator. Start is the DECLARED start and applies at creation only; nothing emits RESTART.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("seq_pos", ", \"Increment\": 1"));
            Exec2(cmd, "SELECT nextval('seq_pos') FROM generate_series(1, 25)");
            cmd.CommandText = "SELECT last_value FROM seq_pos";
            var consumed = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.That(consumed, Is.EqualTo(25), "precondition: the sequence has been consumed");

            // A deploy that changes something else entirely must not disturb the position.
            Deploy(cmd, Package("seq_pos", ", \"Increment\": 1, \"Cache\": 5"));

            cmd.CommandText = "SELECT last_value FROM seq_pos";
            Assert.That(Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(consumed),
                "a deploy must never move a live sequence's position");
        });
    }

    [Test]
    public void ASerialColumnsSequence_IsNotExtracted()
    {
        // Issue #409. A serial column's sequence is recorded with deptype 'a', an IDENTITY column's with
        // 'i'. The old filter caught only 'i', so every serial sequence was extracted as a standalone
        // object -- and on redeploy the package created it first, leaving CREATE TABLE ... serial to
        // generate <name>1 and point the column at that instead.
        OnDb(cmd =>
        {
            Exec2(cmd, "DROP TABLE IF EXISTS owned_probe CASCADE");
            Exec2(cmd, "CREATE TABLE owned_probe (id serial PRIMARY KEY, other int GENERATED ALWAYS AS IDENTITY)");

            cmd.CommandText = @"SELECT COUNT(*) FROM pg_class s
                                  JOIN pg_namespace n ON n.oid = s.relnamespace
                                 WHERE s.relkind = 'S' AND n.nspname = 'public'
                                   AND s.relname LIKE 'owned_probe%'
                                   AND NOT EXISTS (SELECT 1 FROM pg_depend d
                                                    WHERE d.objid = s.oid
                                                      AND d.classid = 'pg_class'::regclass
                                                      AND d.deptype IN ('i', 'a'))";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "neither the serial nor the IDENTITY sequence may be seen as standalone");
        });
    }

    [Test]
    public void ItRoundTripsThroughExtraction_WithoutTheCurrentValue()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("seq_round", ", \"Increment\": 4, \"MaxValue\": 400"));
            Exec2(cmd, "SELECT nextval('seq_round')");

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateSequenceJSON\"('public', 'seq_round')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("\"Increment\": 4"), json);
                Assert.That(json, Does.Contain("\"MaxValue\": 400"), json);
                Assert.That(json, Does.Not.Contain("last_value"),
                    "the current value is data, not schema -- capturing it would reset a live sequence.\n" + json);
            });
        });
    }

    [Test]
    public void WhatIf_ChangesNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("seq_whatif"), whatIf: true);

            cmd.CommandText = "SELECT COUNT(*) FROM pg_class WHERE relkind = 'S' AND relname = 'seq_whatif'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero);
        });
    }

    private static void Exec2(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
