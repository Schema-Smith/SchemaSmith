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
/// PostgreSQL <c>TABLESPACE</c> placement for tables and indexes.
/// <para><b>The gap was an asymmetry, not an oversight in principle.</b>
/// <c>PostgreSqlMaterializedView.Tablespace</c> has been wired end to end since materialized views
/// shipped — extraction, deserialization and DDL. So a matview could declare where it lives and a table
/// could not, which is an accident of what got built rather than a decision.</para>
/// <para><b>Posture is SQL Server's <c>FileGroup</c>, deliberately.</b> Unset means "SchemaSmith does not
/// manage placement here" — <i>not</i> a declaration of the database default. Reading unset as "the
/// default" is what made every DBA-placed object fail its SECOND deploy in packages that never mentioned
/// placement, and <see cref="AnUndeclaredTablespace_LeavesAnObjectWhereItIs"/> is the test that keeps
/// that from coming back. A declared tablespace that differs from where the object already lives is a
/// MOVE — a full rewrite under an ACCESS EXCLUSIVE lock — so it is refused by name rather than performed.</para>
/// <para>The fixture creates a real tablespace; there is no way to test placement without one.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class TablespaceTests
{
    private const string TablespaceName = "ss_ts_test";
    private const string TablespacePath = "/var/lib/postgresql/ss_ts_test";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _props = new();
    private string _db = "";
    private bool _tablespaceReady;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["PostgreSQL:Server"] ?? "127.0.0.1";
        _user = config["PostgreSQL:User"];
        _password = config["PostgreSQL:Password"];
        _port = config["PostgreSQL:Port"];
        _props = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");

        _db = $"ss_ts_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
        using var maint = Open("postgres");
        Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        Exec(maint, $"CREATE DATABASE \"{_db}\"");

        // CREATE TABLESPACE needs superuser AND a server-side directory that already exists -- PostgreSQL
        // will not create one ("directory ... does not exist", verified). A stock postgres container has no
        // such directory, so relying on one being there means these tests SKIP in CI while reading as a
        // clean run -- the failure mode where an empty run looks like a pass. COPY ... TO PROGRAM makes the
        // fixture self-sufficient: it is superuser-only, which is no extra requirement here because
        // CREATE TABLESPACE is too. Ignore remains only for a server where neither is permitted.
        try
        {
            Exec(maint, $"COPY (SELECT 1) TO PROGRAM 'mkdir -p {TablespacePath}'");
        }
        catch (DbException) { /* directory may already exist, or COPY TO PROGRAM may be blocked */ }

        try
        {
            Exec(maint, $"CREATE TABLESPACE {TablespaceName} LOCATION '{TablespacePath}'");
            _tablespaceReady = true;
        }
        catch (DbException)
        {
            try
            {
                using var cmd = maint.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM pg_tablespace WHERE spcname = '{TablespaceName}'";
                _tablespaceReady = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
            }
            catch (DbException) { _tablespaceReady = false; }
        }

        using var c = Open(_db);
        using var kindle = c.CreateCommand();
        ForgeKindler.KindleTheForge(kindle, Platform.PostgreSQL);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var maint = Open("postgres");
            Exec(maint, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_db}' AND pid <> pg_backend_pid()");
            Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
            // Only droppable once every object in it is gone, which dropping the database achieves.
            Exec(maint, $"DROP TABLESPACE IF EXISTS {TablespaceName}");
        }
        catch (DbException) { /* teardown must not mask an assertion */ }
    }

    [SetUp]
    public void RequireTablespace()
    {
        if (!_tablespaceReady)
            Assert.Ignore($"Tablespace {TablespaceName} could not be created ({TablespacePath} must exist server-side "
                          + "and the test login must be superuser). Placement cannot be tested without one.");
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

    private static string Package(string table, string tableTablespace = null, string indexTablespace = null) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + table + "\","
        + (tableTablespace == null ? "" : " \"Tablespace\": \"" + tableTablespace + "\",")
        + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"val\", \"DataType\": \"text\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"ix_" + table + "\", \"IndexColumns\": \"val\""
        + (indexTablespace == null ? "" : ", \"Tablespace\": \"" + indexTablespace + "\"") + " } ] }]";

    private static void Deploy(IDbCommand cmd, string json)
    {
        cmd.CommandText = "CALL \"SchemaSmith\".\"TableQuench\"('TsTest', $ss$" + json + "$ss$, false, false, false)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Empty string means the database default — pg_class.reltablespace is 0 there.</summary>
    private static string Placement(IDbCommand cmd, string relname)
    {
        cmd.CommandText = "SELECT COALESCE(ts.spcname, '') FROM pg_class c "
                          + "LEFT JOIN pg_tablespace ts ON ts.oid = c.reltablespace "
                          + "WHERE c.relname = '" + relname + "'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    // #571: a tablespace that does not exist failed mid-deploy with 42704, after earlier tables were already created. It is
    // refused before any DDL, naming what declares it, as SQL Server refuses a missing partition scheme. (Whether anything
    // was created is not asserted: the failed CALL rolls its own work back either way.)
    [TestCase("table")]
    [TestCase("index")]
    public void AMissingTablespace_IsRefusedBeforeAnythingIsCreated(string declaredOn)
    {
        OnDb(cmd =>
        {
            var json = "[" + Package("ts_before").Trim('[', ']') + ","
                       + Package("ts_missing", tableTablespace: declaredOn == "table" ? "ss_no_such_ts" : null,
                                 indexTablespace: declaredOn == "index" ? "ss_no_such_ts" : null).Trim('[', ']') + "]";

            var ex = Assert.Catch(() => Deploy(cmd, json));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("ss_no_such_ts").And.Contain(declaredOn == "table" ? "ts_missing" : "ix_ts_missing"));
                Assert.That(ex.Message, Does.Not.Contain("42704"), "the refusal is ours, not the engine's");
            });
        });
    }

    [Test]
    public void AMissingTablespace_OnAnIndexOnlyDeploy_OrAMaterializedView_IsRefusedToo()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_idxonly"));
            var indexOnly = Assert.Catch(() => Exec2(cmd, "CALL \"SchemaSmith\".\"IndexOnlyQuench\"('TsTest', $ss$"
                + Package("ts_idxonly", indexTablespace: "ss_no_such_ts") + "$ss$, false, false, false)"));
            var view = Assert.Catch(() => Exec2(cmd, "CALL \"SchemaSmith\".\"MaterializedViewQuench\"('TsTest', $ss$"
                + "[{ \"Schema\": \"public\", \"Name\": \"ts_mv\", \"Definition\": \"SELECT 1 AS x\", \"Tablespace\": \"ss_no_such_ts\" }]"
                + "$ss$, false, true)"));

            Assert.Multiple(() =>
            {
                Assert.That(indexOnly!.Message, Does.Contain("Tablespace not found").And.Contain("ix_ts_idxonly"));
                Assert.That(view!.Message, Does.Contain("Tablespace not found").And.Contain("ts_mv"));
            });
        });
    }

    [Test]
    public void ADeclaredTableTablespace_PlacesTheTable()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_table", tableTablespace: TablespaceName));

            Assert.That(Placement(cmd, "ts_table"), Is.EqualTo(TablespaceName));
        });
    }

    [Test]
    public void ADeclaredIndexTablespace_PlacesTheIndex()
    {
        // An index does NOT follow its table's tablespace, so this is genuinely a separate declaration
        // rather than something inherited from the table above.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_index", indexTablespace: TablespaceName));

            Assert.Multiple(() =>
            {
                Assert.That(Placement(cmd, "ix_ts_index"), Is.EqualTo(TablespaceName));
                Assert.That(Placement(cmd, "ts_index"), Is.Empty,
                    "the table itself declared nothing, so it must stay on the database default");
            });
        });
    }

    [Test]
    public void AnUndeclaredTablespace_LeavesAnObjectWhereItIs()
    {
        // The contract that matters most, and the one whose absence is only visible on a SECOND deploy.
        // Unset means "not managed" -- if it were read as "the database default", this object, placed
        // out of band exactly as a DBA would, would be reported as drifted and the deploy would fail.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_unmanaged"));
            Exec2(cmd, $"ALTER TABLE public.ts_unmanaged SET TABLESPACE {TablespaceName}");
            Assert.That(Placement(cmd, "ts_unmanaged"), Is.EqualTo(TablespaceName), "precondition");

            Assert.DoesNotThrow(() => Deploy(cmd, Package("ts_unmanaged")),
                "a package that never mentions placement must not fail against an object someone placed");

            Assert.That(Placement(cmd, "ts_unmanaged"), Is.EqualTo(TablespaceName),
                "and it must certainly not be moved back");
        });
    }

    [Test]
    public void ADeclaredMoveOfAnExistingTable_IsRefusedByName()
    {
        // Moving rewrites the whole table under an ACCESS EXCLUSIVE lock. Doing that silently because a
        // string changed in a package is the failure this refusal exists to prevent.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_move"));
            Assert.That(Placement(cmd, "ts_move"), Is.Empty, "precondition: on the database default");

            var ex = Assert.Catch(() => Deploy(cmd, Package("ts_move", tableTablespace: TablespaceName)));

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex.Message, Does.Contain("ts_move").And.Contain(TablespaceName),
                "the refusal has to name the object and both placements, or nobody can act on it.\n" + ex.Message);
            Assert.That(Placement(cmd, "ts_move"), Is.Empty, "and it must not have moved");
        });
    }

    [Test]
    public void ADeclaredMoveOfAnExistingIndex_IsRefusedByName()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_imove"));

            var ex = Assert.Catch(() => Deploy(cmd, Package("ts_imove", indexTablespace: TablespaceName)));

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex.Message, Does.Contain("ix_ts_imove"), ex.Message);
        });
    }

    // #545: an object on the database default records tablespace 0 whatever it was created with, so a declared
    // pg_default was read as a move -- on the very deploy that created the table, and on every one after.
    [Test]
    public void ADeclaredPgDefault_DeploysAndRedeploys()
    {
        OnDb(cmd =>
        {
            Assert.DoesNotThrow(() => Deploy(cmd, Package("ts_pgdef", tableTablespace: "pg_default", indexTablespace: "pg_default")),
                "pg_default is where the table is created, not a move");
            Assert.DoesNotThrow(() => Deploy(cmd, Package("ts_pgdef", tableTablespace: "pg_default", indexTablespace: "pg_default")));
            Assert.That(Placement(cmd, "ts_pgdef"), Is.Empty);
        });
    }

    [Test]
    public void AnExistingTableOnTheDefault_LaterDeclaredPgDefault_IsNotAMove()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_pgdef_late"));
            Assert.DoesNotThrow(() => Deploy(cmd, Package("ts_pgdef_late", tableTablespace: "pg_default", indexTablespace: "pg_default")));
        });
    }

    [Test]
    public void ATableElsewhere_DeclaredPgDefault_IsStillRefusedAsAMove()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_pgdef_back", tableTablespace: TablespaceName));

            var ex = Assert.Catch(() => Deploy(cmd, Package("ts_pgdef_back", tableTablespace: "pg_default")));

            Assert.That(ex?.Message, Does.Contain("pg_default").And.Contain(TablespaceName), ex?.Message);
            Assert.That(Placement(cmd, "ts_pgdef_back"), Is.EqualTo(TablespaceName), "and it is not moved");
        });
    }

    [Test]
    public void RedeployingAnUnchangedDeclaration_IsIdempotent()
    {
        // The declared-and-already-correct case must NOT trip the move refusal -- that would make a
        // package that placed an object correctly fail every deploy after the first.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_idem", tableTablespace: TablespaceName, indexTablespace: TablespaceName));

            Assert.DoesNotThrow(() => Deploy(cmd, Package("ts_idem", tableTablespace: TablespaceName, indexTablespace: TablespaceName)));

            Assert.Multiple(() =>
            {
                Assert.That(Placement(cmd, "ts_idem"), Is.EqualTo(TablespaceName));
                Assert.That(Placement(cmd, "ix_ts_idem"), Is.EqualTo(TablespaceName));
            });
        });
    }

    [Test]
    public void TablespacesRoundTripThroughExtraction()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_round", tableTablespace: TablespaceName, indexTablespace: TablespaceName));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', 'ts_round')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.That(json, Does.Contain("Tablespace").And.Contain(TablespaceName),
                "an extracted package that drops placement redeploys the object somewhere else.\n" + json);
        });
    }

    [Test]
    public void AnObjectOnTheDatabaseDefault_ExtractsWithoutTheProperty()
    {
        // reltablespace 0 is not a placement. Emitting it as one would rewrite every committed PostgreSQL
        // package, and would then read back as a declaration that pins the object to today's default.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("ts_default"));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', 'ts_default')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.That(json, Does.Not.Contain("Tablespace"), json);
        });
    }

    private static void Exec2(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
