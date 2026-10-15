// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// A check constraint's and foreign key's <c>Validated</c> is <c>convalidated</c>, a check's <c>NoInherit</c> is
/// <c>connoinherit</c>, and <c>Enforced</c> is PostgreSQL 18's <c>conenforced</c> -- degraded below 18 under
/// <c>UnsupportedFeaturePolicy</c>. A domain check's <c>Validated</c> is the same flag on the domain's constraint. Every
/// assertion reads the catalog, never the file. Uses the suite's kindled database and isolates by object name; the
/// 18-only path runs when the suite is pointed at an 18 server and the degrade when it is not, so both are covered
/// across the two runs the floor sweep already makes.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ConstraintAttributeTests
{
    private const string Product = "CaTest";
    private const string Parent = "ca_parent";
    private const string Table = "ca_orders";
    private const string Domain = "ca_qty";
    private const string Fk = "fk_ca_orders_parent";
    private const string Ck = "ck_ca_orders_qty";
    private IDbConnection _connection;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        DropOwnObjects();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try { DropOwnObjects(); }
        finally { _connection.Close(); _connection.Dispose(); }
    }

    [SetUp]
    public void SetUp()
    {
        DropOwnObjects();
        Exec("RESET schemasmith.unsupported_policy");
        Exec($"DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE 'public.ca_%'");
    }

    private void DropOwnObjects() => Exec($"DROP TABLE IF EXISTS public.{Table}; DROP TABLE IF EXISTS public.{Parent}; DROP DOMAIN IF EXISTS public.{Domain};");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string Str(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private int Count(string sql) => Convert.ToInt32(Str(sql) ?? "0");

    private int ServerMajor() => Convert.ToInt32(Str("SELECT current_setting('server_version_num')::int / 10000"));

    private void Deploy(string json, bool whatIf = false) =>
        Exec("CALL \"SchemaSmith\".\"TableQuench\"('" + Product + "', $ss$" + json + "$ss$, " + (whatIf ? "true" : "false") + ", false, false)");

    private void DeployDomain(string json) => Exec("CALL \"SchemaSmith\".\"DomainTypeQuench\"('" + Product + "', $ss$" + json + "$ss$, false)");

    /// <param name="fk">Attribute keys appended to the foreign key, e.g. <c>, "Validated": false</c>.</param>
    /// <param name="ck">Attribute keys appended to the check constraint.</param>
    private static string Package(string fk = "", string ck = "", bool withFk = true, bool withCheck = true) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + Parent + "\", \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + Parent + "\", \"IndexColumns\": \"id\", \"PrimaryKey\": true, \"Unique\": true } ] },"
        + " { \"Schema\": \"public\", \"Name\": \"" + Table + "\", \"Columns\": ["
        + " { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"parent_id\", \"DataType\": \"integer\", \"Nullable\": true },"
        + " { \"Name\": \"qty\", \"DataType\": \"integer\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + Table + "\", \"IndexColumns\": \"id\", \"PrimaryKey\": true, \"Unique\": true } ]"
        + (withFk ? ", \"ForeignKeys\": [ { \"Name\": \"" + Fk + "\", \"Columns\": \"parent_id\", \"RelatedTableSchema\": \"public\", \"RelatedTable\": \"" + Parent + "\", \"RelatedColumns\": \"id\"" + fk + " } ]" : "")
        + (withCheck ? ", \"CheckConstraints\": [ { \"Name\": \"" + Ck + "\", \"Expression\": \"qty >= 0 OR parent_id IS NULL\"" + ck + " } ]" : "")
        + " }]";

    private static string DomainPackage(string attrs = "") =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + Domain + "\", \"DataType\": \"integer\", \"CheckConstraints\": [ { \"Name\": \"ck_" + Domain + "_positive\", \"Expression\": \"VALUE > 0\"" + attrs + " } ] }]";

    private (bool Validated, bool NoInherit, bool Enforced) State(string name)
    {
        var s = Str($"SELECT convalidated::text || ',' || connoinherit::text || ',' || \"SchemaSmith\".\"ConstraintEnforced\"(oid)::text FROM pg_constraint WHERE conrelid = 'public.{Table}'::regclass AND conname = '{name}'");
        Assert.That(s, Is.Not.Null, $"{name} must exist");
        var p = s.Split(',');
        return (p[0] == "true", p[1] == "true", p[2] == "true");
    }

    /// <summary>A foreign key is never inherited, so its connoinherit is always true and says nothing: only the two states that matter.</summary>
    private (bool Validated, bool Enforced) FkState()
    {
        var s = State(Fk);
        return (s.Validated, s.Enforced);
    }

    private bool DomainValidated() => Str($"SELECT convalidated::text FROM pg_constraint WHERE contypid = 'public.{Domain}'::regtype AND conname = 'ck_{Domain}_positive'") == "true";

    private int Audits(string objectType, string action, string name) =>
        Count($"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectType\" = '{objectType}' AND \"ActionType\" = '{action}' AND \"ObjectName\" = 'public.{Table}.{name}'");

    private void ClearAudit() => Exec($"DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" LIKE 'public.ca_%'");

    [Test]
    public void DeclaredAttributes_ArriveInTheCatalog_AndARedeployRecordsNothing()
    {
        Deploy(Package(fk: ", \"Validated\": false", ck: ", \"Validated\": false, \"NoInherit\": true"));

        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((false, true)), "foreign key: NOT VALID");
            Assert.That(State(Ck), Is.EqualTo((false, true, true)), "check: NOT VALID and NO INHERIT");
        });

        ClearAudit();
        Deploy(Package(fk: ", \"Validated\": false", ck: ", \"Validated\": false, \"NoInherit\": true"));
        Assert.That(Count($"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" LIKE 'public.{Table}.%'"), Is.Zero,
            "the same declaration a second time must touch nothing");
    }

    [Test]
    public void OmittedAttributes_AreTheDefaults()
    {
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((true, true)));
            Assert.That(State(Ck), Is.EqualTo((true, false, true)));
        });
    }

    [Test]
    public void ValidatedTrue_OverANotValidCheck_ChecksTheRows_AndFailsOnAViolation()
    {
        Deploy(Package(withCheck: false));
        Exec($"INSERT INTO public.{Parent} (id) VALUES (1); INSERT INTO public.{Table} (id, parent_id, qty) VALUES (1, 1, -5)");
        Deploy(Package(ck: ", \"Validated\": false"));
        Assert.That(State(Ck).Validated, Is.False, "setup: created NOT VALID over a violating row");
        ClearAudit();

        var ex = Assert.Catch<DbException>(() => Deploy(Package()), "validating a constraint the rows violate must fail, not silently skip");
        Assert.That(ex!.Message, Does.Contain(Ck));
        Assert.That(State(Ck).Validated, Is.False, "still NOT VALID after the failed validation");

        Exec($"DELETE FROM public.{Table} WHERE id = 1");
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(State(Ck).Validated, Is.True, "with the violation gone, the rows validate");
            Assert.That(Audits("constraint", "modified", Ck), Is.EqualTo(1));
        });
    }

    [Test]
    public void ValidatedTrue_OverANotValidForeignKey_ChecksTheRows_AndFailsOnAViolation()
    {
        Deploy(Package(withFk: false));
        Exec($"INSERT INTO public.{Table} (id, parent_id, qty) VALUES (1, 99, 1)");
        Deploy(Package(fk: ", \"Validated\": false"));
        Assert.That(State(Fk).Validated, Is.False, "setup: created NOT VALID over an orphan row");

        var ex = Assert.Catch<DbException>(() => Deploy(Package()));
        Assert.That(ex!.Message, Does.Contain(Fk));

        Exec($"DELETE FROM public.{Table} WHERE id = 1");
        Deploy(Package());
        Assert.That(State(Fk).Validated, Is.True);
    }

    [Test]
    public void ValidatedFalse_OverAValidatedConstraint_ChangesNothing()
    {
        Deploy(Package());
        ClearAudit();

        Deploy(Package(fk: ", \"Validated\": false", ck: ", \"Validated\": false"));

        Assert.Multiple(() =>
        {
            Assert.That(State(Fk).Validated, Is.True, "the engine cannot un-validate, and nothing should try");
            Assert.That(State(Ck).Validated, Is.True);
            Assert.That(Count($"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" LIKE 'public.{Table}.%'"), Is.Zero);
        });
    }

    [Test]
    public void NoInheritChange_DropsAndRecreates_TheCheck()
    {
        Deploy(Package());
        ClearAudit();

        Deploy(Package(ck: ", \"NoInherit\": true"));

        Assert.Multiple(() =>
        {
            Assert.That(State(Ck).NoInherit, Is.True);
            Assert.That(Audits("constraint", "dropped", Ck), Is.EqualTo(1), "create-time state: the check is re-created");
            Assert.That(Audits("constraint", "created", Ck), Is.EqualTo(1));
        });

        ClearAudit();
        Deploy(Package(ck: ", \"NoInherit\": true"));
        Assert.That(Audits("constraint", "dropped", Ck), Is.Zero, "and not again on the next deploy");
    }

    [Test]
    public void EnforcedFalse_IsHonouredOn18_AndDowngradedUnderThePolicyBelowIt()
    {
        Deploy(Package());
        ClearAudit();

        if (ServerMajor() >= 18)
        {
            Deploy(Package(fk: ", \"Enforced\": false", ck: ", \"Enforced\": false"));
            Assert.Multiple(() =>
            {
                Assert.That(State(Fk).Enforced, Is.False, "ALTER CONSTRAINT ... NOT ENFORCED");
                Assert.That(State(Ck).Enforced, Is.False);
                Assert.That(Audits("foreignKey", "modified", Fk), Is.EqualTo(1), "a foreign key's enforceability is altered in place");
                Assert.That(Audits("constraint", "dropped", Ck) + Audits("constraint", "created", Ck), Is.EqualTo(2), "a check's enforceability cannot be altered, so it is re-created");
            });

            ClearAudit();
            Deploy(Package(fk: ", \"Enforced\": false", ck: ", \"Enforced\": false"));
            Assert.That(Audits("foreignKey", "modified", Fk) + Audits("constraint", "modified", Ck) + Audits("constraint", "dropped", Ck), Is.Zero, "a redeploy touches nothing");

            Deploy(Package());
            Assert.Multiple(() =>
            {
                Assert.That(FkState(), Is.EqualTo((true, true)), "an omitted Enforced is true, and enforcing re-checks the rows");
                Assert.That(State(Ck), Is.EqualTo((true, false, true)));
            });
            return;
        }

        Deploy(Package(fk: ", \"Enforced\": false", ck: ", \"Enforced\": false"));
        Assert.Multiple(() =>
        {
            Assert.That(State(Fk).Enforced, Is.True, "below 18 the constraint deploys enforced");
            Assert.That(State(Ck).Enforced, Is.True);
            Assert.That(Count($"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectType\" = 'NOT ENFORCED constraint (PG18)' AND \"ActionType\" = 'downgraded' AND \"ObjectName\" LIKE 'public.{Table}.%'"),
                Is.EqualTo(2), "one downgrade row per declared-but-unsupported constraint");
        });

        Exec("SET schemasmith.unsupported_policy = 'fail'");
        var ex = Assert.Catch<DbException>(() => Deploy(Package(fk: ", \"Enforced\": false", ck: ", \"Enforced\": false")));
        Assert.That(ex!.Message, Does.Contain("NOT ENFORCED constraints require PostgreSQL 18").And.Contain(Ck).And.Contain(Fk));
    }

    [Test]
    public void WhatIf_RecordsWhatItWouldChange_AndChangesNothing()
    {
        Deploy(Package(withCheck: false));
        Deploy(Package(ck: ", \"Validated\": false"));
        ClearAudit();

        Deploy(Package(), whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(State(Ck).Validated, Is.False, "WhatIf must not validate it");
            Assert.That(Audits("constraint", "wouldModify", Ck), Is.EqualTo(1));
            Assert.That(Audits("constraint", "modified", Ck), Is.Zero);
        });
    }

    [Test]
    public void Extraction_ReadsTheAttributes_AndKeepsTheSuffixesOutOfTheExpression()
    {
        Deploy(Package(fk: ", \"Validated\": false", ck: ", \"Validated\": false, \"NoInherit\": true"));

        var table = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(Str($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}')"), Platform.PostgreSQL);
        var fk = (PostgreSqlForeignKey)table.ForeignKeys.Single();
        var ck = (PostgreSqlCheckConstraint)table.CheckConstraints.Single();
        Assert.Multiple(() =>
        {
            Assert.That((fk.Validated, fk.Enforced), Is.EqualTo((false, true)));
            Assert.That((ck.Validated, ck.NoInherit, ck.Enforced), Is.EqualTo((false, true, true)));
            Assert.That(ck.Expression, Does.Not.Contain("NOT VALID").And.Not.Contain("NO INHERIT"), "the suffixes are attributes now, not expression text: " + ck.Expression);
        });

        Deploy(Package());
        table = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(Str($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}')"), Platform.PostgreSQL);
        Assert.Multiple(() =>
        {
            Assert.That(((PostgreSqlForeignKey)table.ForeignKeys.Single()).Validated, Is.True);
            Assert.That(((PostgreSqlCheckConstraint)table.CheckConstraints.Single()).NoInherit, Is.False, "NoInherit is create-time state, so the re-created check reads false");
        });
    }

    [Test]
    public void ADomainCheck_CreatedNotValid_IsValidatedWhenDeclaredValid_AndExtractsItsState()
    {
        DeployDomain(DomainPackage());
        Exec($"CREATE TABLE public.{Table} (id integer, q public.{Domain})");
        Exec($"ALTER DOMAIN public.{Domain} DROP CONSTRAINT ck_{Domain}_positive; INSERT INTO public.{Table} VALUES (1, -1)");

        DeployDomain(DomainPackage(", \"Validated\": false"));
        Assert.That(DomainValidated(), Is.False, "added NOT VALID over a violating value");

        var dom = JsonText.Deserialize<PostgreSqlDomainType>(Str($"SELECT \"SchemaSmith\".\"GenerateDomainTypeJSON\"('public', '{Domain}')"));
        Assert.Multiple(() =>
        {
            Assert.That(dom.CheckConstraints.Single().Validated, Is.False);
            Assert.That(dom.CheckConstraints.Single().Expression, Does.Not.Contain("NOT VALID"));
        });

        var ex = Assert.Catch<DbException>(() => DeployDomain(DomainPackage()));
        Assert.That(ex!.Message, Does.Contain("violate"), "PostgreSQL names the column, not the domain constraint, when a domain validation fails: " + ex.Message);

        Exec($"DELETE FROM public.{Table}");
        DeployDomain(DomainPackage());
        Assert.That(DomainValidated(), Is.True);
    }
}
