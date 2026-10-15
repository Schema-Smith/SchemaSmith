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
/// PostgreSQL domain types, promoted from scripted to declarative (F5).
/// <para><b>It is promoted because it has storage</b> — real columns are typed by it. A scripted object
/// re-runs unconditionally on every deploy, which is cheap for a procedure and is not cheap for something
/// columns depend on.</para>
/// <para><b>And the scripted form here cannot be made idempotent.</b> There is no
/// <c>CREATE OR REPLACE DOMAIN</c>, so a scripted domain is a guarded <c>CREATE DOMAIN</c> that skips once
/// the domain exists. <see cref="TheScriptedFormsSilentNoOp_IsWhatThisReplaces"/> pins that engine
/// behaviour so the reason this exists cannot be lost.</para>
/// <para><b>Unlike an enum, almost everything converges.</b> <c>ALTER DOMAIN</c> adds and drops
/// constraints, sets and drops the default, and sets and drops NOT NULL — none of which drops the domain
/// or touches a dependent column. The base type is the one exception and is refused by name.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class DomainTypeTests
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

        _db = $"ss_domain_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
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

    private static void Exec2(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>One domain, optionally with a NOT NULL, a default, and any number of CHECK constraints.</summary>
    private static string Package(string name, string dataType, bool notNull = false, string def = null,
                                  params (string Name, string Expression)[] checks)
    {
        var checkJson = string.Join(", ", Array.ConvertAll(checks,
            c => "{ \"Name\": \"" + c.Name + "\", \"Expression\": \"" + c.Expression.Replace("\"", "\\\"") + "\" }"));
        return "[{ \"Schema\": \"public\", \"Name\": \"" + name + "\", \"DataType\": \"" + dataType + "\""
               + ", \"NotNull\": " + (notNull ? "true" : "false")
               + (def is null ? "" : ", \"Default\": \"" + def + "\"")
               + ", \"CheckConstraints\": [" + checkJson + "] }]";
    }

    private static void Deploy(IDbCommand cmd, string json, bool whatIf = false)
    {
        cmd.CommandText = "CALL \"SchemaSmith\".\"DomainTypeQuench\"('DomainTest', $ss$" + json + "$ss$, "
                          + (whatIf ? "true" : "false") + ")";
        cmd.ExecuteNonQuery();
    }

    /// <summary>The domain's CHECK constraints, by name. contype = 'c' only -- see the next helper.</summary>
    private static string Checks(IDbCommand cmd, string name)
    {
        cmd.CommandText =
            "SELECT COALESCE(STRING_AGG(c.conname, ',' ORDER BY c.conname), '') FROM pg_constraint c "
            + "JOIN pg_type t ON t.oid = c.contypid WHERE t.typname = '" + name + "' AND c.contype = 'c'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    private static string BaseType(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT FORMAT_TYPE(t.typbasetype, t.typtypmod) FROM pg_type t WHERE t.typname = '" + name + "'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    private static bool NotNull(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT t.typnotnull FROM pg_type t WHERE t.typname = '" + name + "'";
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static string Default(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT COALESCE(PG_GET_EXPR(t.typdefaultbin, 0), '') FROM pg_type t WHERE t.typname = '" + name + "'";
        return Convert.ToString(cmd.ExecuteScalar());
    }

    [Test]
    public void TheScriptedFormsSilentNoOp_IsWhatThisReplaces()
    {
        // Not a test of SchemaSmith -- a test of the ENGINE behaviour that made the scripted form useless,
        // pinned so the reason this feature exists cannot be lost. There is no CREATE OR REPLACE DOMAIN, so
        // a scripted domain is a guarded CREATE, and the guard skips once the domain exists.
        OnDb(cmd =>
        {
            Exec2(cmd, "DROP DOMAIN IF EXISTS scripted_d CASCADE");
            Exec2(cmd, "CREATE DOMAIN scripted_d AS int CHECK (VALUE > 0)");
            Exec2(cmd, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname='scripted_d') "
                       + "THEN CREATE DOMAIN scripted_d AS int CHECK (VALUE > 100); END IF; END $$");

            cmd.CommandText = "SELECT pg_get_constraintdef(c.oid) FROM pg_constraint c "
                              + "JOIN pg_type t ON t.oid = c.contypid WHERE t.typname = 'scripted_d' AND c.contype = 'c'";
            Assert.That(Convert.ToString(cmd.ExecuteScalar()), Does.Contain("> 0"),
                "the guarded re-create silently does nothing -- the edited CHECK never lands, and this is "
                + "the failure the declarative form fixes");
        });
    }

    [Test]
    public void ADeclaredDomain_IsCreatedWithEverythingItDeclares()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("age_t", "integer", notNull: true, def: "0", checks: ("age_positive", "VALUE >= 0")));

            Assert.Multiple(() =>
            {
                Assert.That(BaseType(cmd, "age_t"), Is.EqualTo("integer"));
                Assert.That(NotNull(cmd, "age_t"), Is.True);
                Assert.That(Default(cmd, "age_t"), Is.EqualTo("0"));
                Assert.That(Checks(cmd, "age_t"), Is.EqualTo("age_positive"),
                    "constraints declared on a new domain are created WITH it, so it never exists in a "
                    + "half-declared state");
            });
        });
    }

    [Test]
    public void AnAddedConstraint_IsApplied_EvenWithADependentTable()
    {
        // The realistic case: the domain is not free-floating, it types a column. ALTER DOMAIN adds the
        // constraint in place without dropping the domain or the column.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("score_t", "integer", checks: ("score_min", "VALUE >= 0")));
            Exec2(cmd, "DROP TABLE IF EXISTS scored");
            Exec2(cmd, "CREATE TABLE scored (id int, s score_t)");
            Exec2(cmd, "INSERT INTO scored VALUES (1, 5)");

            Deploy(cmd, Package("score_t", "integer", false, null, ("score_min", "VALUE >= 0"), ("score_max", "VALUE <= 100")));

            Assert.Multiple(() =>
            {
                Assert.That(Checks(cmd, "score_t"), Is.EqualTo("score_max,score_min"));
                cmd.CommandText = "SELECT COUNT(*) FROM scored";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "and the existing row survives");
            });
        });
    }

    [Test]
    public void AConstraintRemovedFromThePackage_IsDropped()
    {
        // The one place this type differs from the enum, and safely so: dropping a CHECK removes a
        // validation rule, destroys no data, and cascades to nothing. An enum value cannot be removed at
        // all without dropping every column using the type, which is why that one only reports.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("range_t", "integer", false, null, ("r_min", "VALUE >= 0"), ("r_max", "VALUE <= 10")));
            Assert.That(Checks(cmd, "range_t"), Is.EqualTo("r_max,r_min"), "precondition");

            Deploy(cmd, Package("range_t", "integer", false, null, ("r_min", "VALUE >= 0")));

            Assert.That(Checks(cmd, "range_t"), Is.EqualTo("r_min"));
        });
    }

    [Test]
    public void NotNullAndDefault_ConvergeInPlace()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("flag_t", "integer"));
            Assert.That(NotNull(cmd, "flag_t"), Is.False, "precondition");

            Deploy(cmd, Package("flag_t", "integer", notNull: true, def: "7"));

            Assert.Multiple(() =>
            {
                Assert.That(NotNull(cmd, "flag_t"), Is.True);
                Assert.That(Default(cmd, "flag_t"), Is.EqualTo("7"));
            });

            // And back off again -- clearing is as much a declaration as setting.
            Deploy(cmd, Package("flag_t", "integer"));

            Assert.Multiple(() =>
            {
                Assert.That(NotNull(cmd, "flag_t"), Is.False);
                Assert.That(Default(cmd, "flag_t"), Is.Empty);
            });
        });
    }

    [Test]
    public void ABaseTypeChange_IsRefusedByName()
    {
        // PostgreSQL has no ALTER DOMAIN ... TYPE at all -- it is a syntax error, not an unsupported
        // operation -- so delivering this would mean dropping the domain and every column using it.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("id_t", "integer"));

            // Assert.Catch, not Throws: Npgsql throws a PostgresException, which DERIVES from DbException,
            // and Throws<T> demands the exact type.
            var ex = Assert.Catch<DbException>(() => Deploy(cmd, Package("id_t", "bigint")));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("id_t"), "the refusal must name the domain");
                Assert.That(ex.Message, Does.Contain("bigint"), "the declared type");
                Assert.That(ex.Message, Does.Contain("integer"), "and the deployed one");
                Assert.That(BaseType(cmd, "id_t"), Is.EqualTo("integer"),
                    "and NOTHING may have changed -- dropping the domain would take every column with it");
            });
        });
    }

    [Test]
    public void RedeployingAnUnchangedDomain_DoesNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("idem_d", "character varying(20)", notNull: true, def: "'x'::character varying",
                checks: ("idem_chk", "VALUE <> ''::character varying")));
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%idem_d%'");

            Deploy(cmd, Package("idem_d", "character varying(20)", notNull: true, def: "'x'::character varying",
                checks: ("idem_chk", "VALUE <> ''::character varying")));

            cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%idem_d%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "an unchanged domain must produce no change at all -- and this is the case that catches a "
                + "base type compared against a differently-rendered form, which would churn forever");
        });
    }

    [Test]
    public void ItRoundTripsThroughExtraction()
    {
        // The extraction filter that matters: PostgreSQL 17 reports a domain's NOT NULL as a pg_constraint
        // row of its own (contype = 'n'), and PostgreSQL 12 -- the floor -- does not. Without the
        // contype = 'c' filter this package would carry a phantom check constraint holding the text
        // "NOT NULL", which is not a valid predicate anywhere.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("round_d", "integer", notNull: true, checks: ("round_chk", "VALUE > 0")));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateDomainTypeJSON\"('public', 'round_d')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("round_chk"), json);
                Assert.That(json, Does.Contain("integer"), json);
                Assert.That(json, Does.Not.Contain("not_null"),
                    "the NOT NULL pseudo-constraint PostgreSQL 17 reports must NOT be extracted as a check "
                    + "constraint -- it does not exist at the floor and cannot be deployed anywhere.\n" + json);
            });
        });
    }

    [Test]
    public void WhatIf_ChangesNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("whatif_d", "integer"), whatIf: true);

            cmd.CommandText = "SELECT COUNT(*) FROM pg_type WHERE typname = 'whatif_d'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "a preview with side effects is the worst of both");
        });
    }

    private static string Comment(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT OBJ_DESCRIPTION(t.oid, 'pg_type') FROM pg_type t WHERE t.typname = '" + name + "' AND t.typtype = 'd'";
        return cmd.ExecuteScalar() as string;
    }

    [Test]
    public void ADeclaredDescription_IsSet_ChangedInPlace_LeftAloneWhenOmitted_AndRemovedByAnEmptyString()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"money_d\", \"DataType\": \"numeric(10,2)\", \"Description\": \"an amount\" }]");
            Assert.That(Comment(cmd, "money_d"), Is.EqualTo("an amount"), "set on create");

            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"money_d\", \"DataType\": \"numeric(10,2)\", \"Description\": \"an amount in dollars\" }]");
            Assert.That(Comment(cmd, "money_d"), Is.EqualTo("an amount in dollars"), "a change converges in place");

            Deploy(cmd, Package("money_d", "numeric(10,2)"));
            Assert.That(Comment(cmd, "money_d"), Is.EqualTo("an amount in dollars"), "omitted means the comment is not managed, so the database's stays");

            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"money_d\", \"DataType\": \"numeric(10,2)\", \"Description\": \"\" }]");
            Assert.That(Comment(cmd, "money_d"), Is.Null, "an empty string is the request to remove it");
        });
    }
}
