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
/// PostgreSQL row-level security policies — gap item D1.
/// <para><b>The gap had teeth.</b> SchemaSmith could already switch RLS on with <c>RowLevelSecurity</c>,
/// but had no way to declare a policy — and a table with RLS enabled and no policy returns <b>no rows</b>
/// to anyone but its owner. Verified on a live server before building this: enable RLS, grant SELECT,
/// read as the grantee, get zero. So the half that shipped could lock a table with no supported way to
/// unlock it.</para>
/// <para>Policies are dropped when they leave the package, and deliberately without an opt-out: a stale
/// policy is a live access-control rule, so leaving one behind is a security posture nobody declared.
/// That is a stronger reason to drop than exists for an index.</para>
/// <para><b>Expressions converge too.</b> A change to an existing policy's USING or WITH CHECK text is applied
/// with <c>ALTER POLICY</c> -- non-destructive, so the table is never left without the rule for an instant -- and
/// adding or removing a clause (which ALTER cannot do) is a DROP + CREATE. PostgreSQL stores the text
/// normalised, which is why this was once left undetected: a raw comparison would re-apply every policy on every
/// deploy. The expression map (#242) is what makes it safe to compare.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class RowLevelSecurityPolicyTests
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

        _db = $"ss_rls_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
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

    private static string Package(string table, string policies) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + table + "\", \"RowLevelSecurity\": true,"
        + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"tenant\", \"DataType\": \"text\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + table + "\", \"IndexColumns\": \"id\", \"PrimaryKey\": true, \"Unique\": true } ],"
        + " \"Policies\": [" + policies + "] }]";

    private static string Policy(string name, string cmd = "ALL", string usingExpr = "true", string withCheck = null) =>
        "{ \"Name\": \"" + name + "\", \"Command\": \"" + cmd + "\", \"UsingExpression\": \"" + usingExpr + "\""
        + (withCheck == null ? "" : ", \"WithCheckExpression\": \"" + withCheck + "\"") + " }";

    private static string PolicyAttr(IDbCommand cmd, string table, string policy, string column)
    {
        cmd.CommandText = "SELECT COALESCE(" + column + ", '(null)') FROM pg_policies WHERE schemaname = 'public'"
                          + " AND tablename = '" + table + "' AND policyname = '" + policy + "'";
        return cmd.ExecuteScalar() as string;
    }

    private static long PolicyOid(IDbCommand cmd, string table, string policy)
    {
        cmd.CommandText = "SELECT COALESCE((SELECT p.oid::bigint FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid"
                          + " WHERE c.relname = '" + table + "' AND p.polname = '" + policy + "'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static int PolicyAuditRows(IDbCommand cmd, string table)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectType\" = 'policy'"
                          + " AND \"ObjectName\" LIKE 'public." + table + ".%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ClearPolicyAudit(IDbCommand cmd, string table)
    {
        cmd.CommandText = "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectType\" = 'policy'"
                          + " AND \"ObjectName\" LIKE 'public." + table + ".%'";
        cmd.ExecuteNonQuery();
    }

    private void Deploy(IDbCommand cmd, string json)
    {
        cmd.CommandText = "CALL \"SchemaSmith\".\"TableQuench\"('RlsTest', $ss$" + json + "$ss$, false, false, false)";
        cmd.ExecuteNonQuery();
    }

    private static int PolicyCount(IDbCommand cmd, string table, string policy = null)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = '" + table + "'"
                          + (policy == null ? "" : " AND policyname = '" + policy + "'");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Test]
    public void ADeclaredPolicy_IsCreated()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_create", Policy("p_all")));

            Assert.Multiple(() =>
            {
                Assert.That(PolicyCount(cmd, "rls_create", "p_all"), Is.EqualTo(1),
                    "without this the table has RLS on and no policy, which returns no rows to anyone but "
                    + "the owner -- the exact hazard this item exists to close");
                cmd.CommandText = "SELECT relrowsecurity FROM pg_class WHERE relname = 'rls_create'";
                Assert.That(cmd.ExecuteScalar(), Is.True, "and RLS itself is still enabled");
            });
        });
    }

    [Test]
    public void APolicyRemovedFromThePackage_IsDropped()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_drop", Policy("p_keep") + "," + Policy("p_remove", "SELECT")));
            Assert.That(PolicyCount(cmd, "rls_drop"), Is.EqualTo(2), "precondition: both policies deployed");

            Deploy(cmd, Package("rls_drop", Policy("p_keep")));

            Assert.Multiple(() =>
            {
                Assert.That(PolicyCount(cmd, "rls_drop", "p_remove"), Is.Zero,
                    "a policy left behind after it leaves the package is a live access-control rule nobody "
                    + "declared");
                Assert.That(PolicyCount(cmd, "rls_drop", "p_keep"), Is.EqualTo(1),
                    "and the one still declared must survive -- an over-broad drop would pass the assertion "
                    + "above while removing every policy on the table");
            });
        });
    }

    [Test]
    public void PoliciesAreIdempotent()
    {
        // The second deploy is the one that finds bugs: a create that does not check for existence fails
        // with "policy already exists", and a drop that misreads the declaration removes what it just made.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_idem", Policy("p_idem")));
            Deploy(cmd, Package("rls_idem", Policy("p_idem")));

            Assert.That(PolicyCount(cmd, "rls_idem", "p_idem"), Is.EqualTo(1));
        });
    }

    [Test]
    public void APolicyRoundTripsThroughExtraction()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_round", Policy("p_round", "SELECT")));

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', 'rls_round')";
            var json = cmd.ExecuteScalar() as string ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("p_round"),
                    "an extracted package that drops the policy re-deploys a table with RLS on and nothing "
                    + "permitting access.\n" + json);
                Assert.That(json, Does.Contain("Policies"), "under a Policies collection");
            });
        });
    }

    [Test]
    public void AWhatIfRun_ReportsThePolicyDrop_WithoutMakingIt()
    {
        // A WhatIf run is how someone decides whether to approve a deploy. A policy drop changes who can
        // read the table, so a preview that stays silent about it is worse than no preview -- the reviewer
        // approves a change they were never shown.
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_whatif", Policy("p_stays") + "," + Policy("p_goes", "SELECT")));

            cmd.CommandText = "CALL \"SchemaSmith\".\"TableQuench\"('RlsTest', $ss$"
                              + Package("rls_whatif", Policy("p_stays")) + "$ss$, true, false, false)";
            cmd.ExecuteNonQuery();

            Assert.Multiple(() =>
            {
                cmd.CommandText = "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" "
                                  + "WHERE \"ObjectType\" = 'policy' AND \"ActionType\" = 'wouldDrop' "
                                  + "AND \"ObjectName\" LIKE '%rls_whatif.p_goes'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "the preview has to name the policy it would remove");
                Assert.That(PolicyCount(cmd, "rls_whatif", "p_goes"), Is.EqualTo(1),
                    "and WhatIf must not actually have dropped it -- a preview with side effects is the "
                    + "worst of both");
            });
        });
    }

    [Test]
    public void ATableWithNoPolicies_GainsNone()
    {
        // The negative half: a create that fired unconditionally would add policies to every table in
        // every package, which on an RLS-enabled table changes who can read it.
        OnDb(cmd =>
        {
            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"rls_none\","
                        + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false } ],"
                        + " \"Indexes\": [ { \"Name\": \"pk_rls_none\", \"IndexColumns\": \"id\", \"PrimaryKey\": true, \"Unique\": true } ] }]");

            Assert.That(PolicyCount(cmd, "rls_none"), Is.Zero);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Expression convergence. These are the cases that used to be a documented "known limit".
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void AChangedUsingExpression_IsApplied_InPlace()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_using", Policy("p_tenant", usingExpr: "tenant = 'a'")));
            var oid = PolicyOid(cmd, "rls_using", "p_tenant");

            Deploy(cmd, Package("rls_using", Policy("p_tenant", usingExpr: "tenant = 'b'")));

            Assert.Multiple(() =>
            {
                Assert.That(PolicyAttr(cmd, "rls_using", "p_tenant", "qual"), Does.Contain("'b'").And.Not.Contain("'a'"),
                    "an edited USING expression must reach the server -- leaving the old one is a live access rule nobody declared");
                Assert.That(PolicyOid(cmd, "rls_using", "p_tenant"), Is.EqualTo(oid),
                    "an expression-only change is an ALTER POLICY, not a drop and re-create: there must be no instant "
                    + "at which the table has RLS on and this policy missing");
            });
        });
    }

    [Test]
    public void AChangedWithCheckExpression_IsApplied()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_check", Policy("p_write", usingExpr: "true", withCheck: "tenant = 'a'")));
            Deploy(cmd, Package("rls_check", Policy("p_write", usingExpr: "true", withCheck: "tenant = 'b'")));

            Assert.That(PolicyAttr(cmd, "rls_check", "p_write", "with_check"), Does.Contain("'b'").And.Not.Contain("'a'"));
        });
    }

    // ALTER POLICY can set a clause but never remove one, so removal must fall back to DROP + CREATE.
    [Test]
    public void RemovingAWithCheckExpression_IsApplied()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_rmcheck", Policy("p_write", usingExpr: "true", withCheck: "tenant = 'a'")));
            Assert.That(PolicyAttr(cmd, "rls_rmcheck", "p_write", "with_check"), Is.Not.EqualTo("(null)"), "precondition");

            Deploy(cmd, Package("rls_rmcheck", Policy("p_write", usingExpr: "true")));

            Assert.That(PolicyAttr(cmd, "rls_rmcheck", "p_write", "with_check"), Is.EqualTo("(null)"),
                "a WITH CHECK removed from the package must be removed from the server");
        });
    }

    // Someone edits the policy by hand. The package did not change; the live rule did. It must be put back.
    [Test]
    public void AnOutOfBandPolicyEdit_IsReApplied()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package("rls_drift", Policy("p_tenant", usingExpr: "tenant = 'a'")));

            cmd.CommandText = "ALTER POLICY p_tenant ON public.rls_drift USING (true)";
            cmd.ExecuteNonQuery();

            Deploy(cmd, Package("rls_drift", Policy("p_tenant", usingExpr: "tenant = 'a'")));

            Assert.That(PolicyAttr(cmd, "rls_drift", "p_tenant", "qual"), Does.Contain("'a'"),
                "a hand-widened policy must be narrowed back to what was declared");
        });
    }

    // The reason this was never compared: PostgreSQL renders "tenant = current_user" as
    // (tenant = (CURRENT_USER)::text). A raw comparison would ALTER every policy on every deploy.
    [Test]
    public void APolicyAuthoredInNaturalForm_IsNotReAppliedOnEveryDeploy()
    {
        OnDb(cmd =>
        {
            var json = Package("rls_quiet", Policy("p_tenant", usingExpr: "tenant = current_user", withCheck: "tenant = current_user"));
            Deploy(cmd, json);
            Assert.That(PolicyAttr(cmd, "rls_quiet", "p_tenant", "qual"), Is.Not.EqualTo("tenant = current_user"),
                "precondition: PostgreSQL must have rewritten the expression, or this proves nothing");
            var oid = PolicyOid(cmd, "rls_quiet", "p_tenant");

            for (var pass = 2; pass <= 3; pass++)
            {
                ClearPolicyAudit(cmd, "rls_quiet");
                Deploy(cmd, json);
                Assert.Multiple(() =>
                {
                    Assert.That(PolicyAuditRows(cmd, "rls_quiet"), Is.Zero,
                        $"pass {pass}: the policy was re-applied for an unchanged declaration");
                    Assert.That(PolicyOid(cmd, "rls_quiet", "p_tenant"), Is.EqualTo(oid), $"pass {pass}: policy identity");
                });
            }
        });
    }

}
