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
/// PostgreSQL <c>REPLICA IDENTITY</c> — issue #407.
/// <para><b>The defect had teeth, and it was a round-trip loss rather than a missing feature.</b> Extraction
/// read neither the setting nor the index it names, so a table extracted from a replicated source and
/// redeployed came back at <c>DEFAULT</c>. On a table that belongs to a publication that is not cosmetic:
/// PostgreSQL <b>refuses</b> <c>UPDATE</c> and <c>DELETE</c> on a published table with no usable replica
/// identity. Two databases whose columns and indexes match, and one of them rejects the application's
/// writes.</para>
/// <para><b>Why this is a separate procedure rather than a clause in ModifiedTableQuench's attribute
/// fixup.</b> The <c>USING INDEX</c> form names an index, and ModifiedTableQuench runs before
/// MissingIndexesAndConstraintsQuench — so on a table's first deploy that index does not exist yet.
/// <see cref="ReplicaIdentityUsingIndex_IsAppliedOnTheFirstDeploy"/> is the test that pins it; put the
/// clause in the fixup block and that test fails while every other test here still passes.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ReplicaIdentityTests
{
    private string _db = "";

    // A schema of its own in the suite's kindled database, not a database: nothing here is a database-level setting, and a
    // database cost a CREATE DATABASE and a kindle per run. Every connection puts the schema first on its search_path.
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"ss_repl_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
        using (var main = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString()))
        {
            main.Open();
            Exec(main, $"CREATE SCHEMA \"{_db}\"");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var main = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
            main.Open();
            Exec(main, $"DROP SCHEMA IF EXISTS \"{_db}\" CASCADE");
        }
        catch (DbException) { /* teardown must not mask an assertion */ }
    }

    private IDbConnection Open(string schema)
    {
        var c = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        c.Open();
        Exec(c, $"SET search_path = \"{schema}\", public");
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

    /// <summary>A table with a unique index over a NOT NULL column — the shape REPLICA IDENTITY USING INDEX requires.</summary>
    private string Package(string table, string replicaIdentity = null, string replicaIndex = null) =>
        "[{ \"Schema\": \"" + _db + "\", \"Name\": \"" + table + "\","
        + (replicaIdentity == null ? "" : " \"ReplicaIdentity\": \"" + replicaIdentity + "\",")
        + (replicaIndex == null ? "" : " \"ReplicaIdentityIndex\": \"" + replicaIndex + "\",")
        + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"val\", \"DataType\": \"text\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"uq_" + table + "\", \"IndexColumns\": \"id\", \"Unique\": true } ] }]";

    private static void Deploy(IDbCommand cmd, string json, bool whatIf = false)
    {
        cmd.CommandText = "CALL \"SchemaSmith\".\"TableQuench\"('RiTest', $ss$" + json + "$ss$, "
                          + (whatIf ? "true" : "false") + ", false, false)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>The catalog's own spelling of the mode: d/f/n/i.</summary>
    private static string Mode(IDbCommand cmd, string table)
    {
        cmd.CommandText = "SELECT relreplident FROM pg_class WHERE relnamespace = current_schema()::regnamespace AND relname = '" + table + "' AND relkind = 'r'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    private static string IdentityIndex(IDbCommand cmd, string table)
    {
        cmd.CommandText = "SELECT ic.relname FROM pg_index ix "
                          + "JOIN pg_class ic ON ic.oid = ix.indexrelid "
                          + "JOIN pg_class tc ON tc.oid = ix.indrelid "
                          + "WHERE tc.relnamespace = current_schema()::regnamespace AND tc.relname = '" + table + "' AND ix.indisreplident";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    [Test]
    public void ADeclaredFullReplicaIdentity_IsApplied()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_full", "FULL"));
            Assert.That(Mode(cmd, "ri_full"), Is.EqualTo("f"));
        });
    }

    [Test]
    public void ADeclaredNothingReplicaIdentity_IsApplied()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_nothing", "NOTHING"));
            Assert.That(Mode(cmd, "ri_nothing"), Is.EqualTo("n"));
        });
    }

    [Test]
    public void ReplicaIdentityUsingIndex_IsAppliedOnTheFirstDeploy()
    {
        // THE ordering test. The index named here is created by MissingIndexesAndConstraintsQuench during
        // this same deploy, so anything applying REPLICA IDENTITY before that pass cannot see it. Apply it
        // from ModifiedTableQuench's attribute fixup and this is the test that goes red.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_first", "INDEX", "uq_ri_first"));

            Assert.Multiple(() =>
            {
                Assert.That(Mode(cmd, "ri_first"), Is.EqualTo("i"),
                    "a first deploy must not silently leave the table at DEFAULT because the index it "
                    + "names had not been created yet");
                Assert.That(IdentityIndex(cmd, "ri_first"), Is.EqualTo("uq_ri_first"));
            });
        });
    }

    [Test]
    public void AnUndeclaredReplicaIdentity_LeavesTheServerSettingAlone()
    {
        // The no-churn contract. Every package written before this shipped omits the property, and every
        // package extraction produces for a DEFAULT table still omits it. Neither may reset a server that
        // was deliberately set to FULL out of band.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_untouched"));
            Exec2(cmd, "ALTER TABLE ri_untouched REPLICA IDENTITY FULL");

            Deploy(cmd, Package("ri_untouched"));

            Assert.That(Mode(cmd, "ri_untouched"), Is.EqualTo("f"),
                "an omitted ReplicaIdentity means 'not managed', not 'reset to DEFAULT'");
        });
    }

    [Test]
    public void SwitchingTheIdentityIndex_IsDetected()
    {
        // relreplident stays 'i' across this change, so a diff comparing only the mode sees nothing to do
        // and the table keeps publishing the wrong key. Both indexes are declared in the package, which is
        // how an author actually moves the identity -- and keeps this test about replica identity rather
        // than about how index reconciliation handles two indexes with identical definitions.
        const string twoIndexes =
            " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
            + " { \"Name\": \"code\", \"DataType\": \"text\", \"Nullable\": false } ],"
            + " \"Indexes\": [ { \"Name\": \"uq_ri_switch\", \"IndexColumns\": \"id\", \"Unique\": true },"
            + " { \"Name\": \"uq_ri_switch_alt\", \"IndexColumns\": \"code\", \"Unique\": true } ] }]";

        string Pkg(string identityIndex) =>
            "[{ \"Schema\": \"" + _db + "\", \"Name\": \"ri_switch\", \"ReplicaIdentity\": \"INDEX\","
            + " \"ReplicaIdentityIndex\": \"" + identityIndex + "\","
            + twoIndexes;

        OnDb(cmd =>
        {
            Deploy(cmd, Pkg("uq_ri_switch"));
            Assert.That(IdentityIndex(cmd, "ri_switch"), Is.EqualTo("uq_ri_switch"), "precondition");

            Deploy(cmd, Pkg("uq_ri_switch_alt"));

            Assert.Multiple(() =>
            {
                Assert.That(Mode(cmd, "ri_switch"), Is.EqualTo("i"), "still index mode on both sides");
                Assert.That(IdentityIndex(cmd, "ri_switch"), Is.EqualTo("uq_ri_switch_alt"),
                    "the mode matched across the change, so only an index-level comparison catches this");
            });
        });
    }

    [Test]
    public void ReplicaIdentityIsIdempotent()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_idem", "FULL"));
            Deploy(cmd, Package("ri_idem", "FULL"));

            Assert.That(Mode(cmd, "ri_idem"), Is.EqualTo("f"));
        });
    }

    [Test]
    public void ReplicaIdentityRoundTripsThroughExtraction()
    {
        // The #407 defect itself: extraction dropped the setting, so the redeployed table lost it.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_round", "INDEX", "uq_ri_round"));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"(current_schema()::text, 'ri_round')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("ReplicaIdentity"),
                    "an extracted package that drops this redeploys a table that can reject UPDATE.\n" + json);
                Assert.That(json, Does.Contain("uq_ri_round"),
                    "and the index carrying the identity has to survive too -- the mode alone does not "
                    + "reconstruct it.\n" + json);
            });
        });
    }

    [Test]
    public void ATableAtTheDefault_ExtractsWithoutTheProperty()
    {
        // The other half of no-churn: emitting "DEFAULT" for every table would rewrite every committed
        // PostgreSQL package and every demo fixture for a setting nobody declared.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_default"));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"(current_schema()::text, 'ri_default')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.That(json, Does.Not.Contain("ReplicaIdentity"), json);
        });
    }

    [Test]
    public void APublishedTable_AcceptsUpdateAfterTheIdentityIsDeployed()
    {
        // The outcome test rather than the mechanism test. Everything above asserts catalog state; this
        // asserts the thing a user actually notices, which is that their application's UPDATE works.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_pub_tbl"));
            Exec2(cmd, "INSERT INTO ri_pub_tbl VALUES (1, 'a')");
            Exec2(cmd, "DROP PUBLICATION IF EXISTS ri_pub");
            Exec2(cmd, "CREATE PUBLICATION ri_pub FOR TABLE ri_pub_tbl");

            // Precondition: at DEFAULT with no primary key, PostgreSQL refuses the write outright.
            var refused = Assert.Catch(() => Exec2(cmd, "UPDATE ri_pub_tbl SET val = 'b' WHERE id = 1"));
            Assert.That(refused, Is.Not.Null, "precondition: the published table must reject UPDATE at DEFAULT");
            Assert.That(refused.Message, Does.Contain("replica identity"), refused.Message);

            Deploy(cmd, Package("ri_pub_tbl", "INDEX", "uq_ri_pub_tbl"));

            Assert.DoesNotThrow(() => Exec2(cmd, "UPDATE ri_pub_tbl SET val = 'b' WHERE id = 1"),
                "deploying the declared replica identity is what makes the application's UPDATE legal again");

            Exec2(cmd, "DROP PUBLICATION IF EXISTS ri_pub");
        });
    }

    [Test]
    public void DeclaringIndexModeWithoutAnIndex_FailsWithANamedError()
    {
        OnDb(cmd =>
        {
            var ex = Assert.Catch(() => Deploy(cmd, Package("ri_bad", "INDEX")));

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex.Message, Does.Contain("ri_bad").And.Contain("ReplicaIdentityIndex"),
                "a package error has to name the table that carries it, not surface as a raw PostgreSQL "
                + "syntax complaint.\n" + ex.Message);
        });
    }

    [Test]
    public void AWhatIfRun_DoesNotChangeTheIdentity()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ri_whatif"));
            Assert.That(Mode(cmd, "ri_whatif"), Is.EqualTo("d"), "precondition");

            Deploy(cmd, Package("ri_whatif", "FULL"), whatIf: true);

            Assert.That(Mode(cmd, "ri_whatif"), Is.EqualTo("d"),
                "a preview with side effects is the worst of both");
        });
    }

    private static void Exec2(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
