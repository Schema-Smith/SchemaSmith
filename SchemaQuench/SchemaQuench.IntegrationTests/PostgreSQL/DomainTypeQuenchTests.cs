// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// PostgreSQL declarative domain types (2.6.0 F5).
///
/// <para><b>Type aliases.</b> The base-type comparison lowercased both sides but did not resolve aliases,
/// and PostgreSQL canonicalises them on storage -- a domain declared <c>VARCHAR(256)</c> is reported by
/// <c>format_type</c> as <c>character varying(256)</c> and could never compare equal. The run CREATED the
/// domain and then refused the domain it had just created, telling the user to "migrate it with a script"
/// for an object that had not existed a moment earlier. <c>TEXT</c> deployed fine while <c>INT</c> did
/// not, which is the controlled comparison that ruled out case and isolated aliasing.</para>
///
/// <para><b>Check expressions.</b> Constraints were reconciled BY NAME ONLY -- the expression was never
/// compared -- so editing one while leaving its name alone was silently ignored at exit 0. That is the
/// exact failure mode the declarative form exists to replace: a user who moved a guarded
/// <c>CREATE DOMAIN</c> script into a package to escape the silent no-op got the silent no-op back, now
/// with a success report and no script to blame.</para>
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class DomainTypeQuenchTests : BaseTableQuenchTests
{
    // ---- type aliases ---------------------------------------------------------

    [TestCase("VARCHAR(256)", "character varying(256)")]
    [TestCase("INT", "integer")]
    [TestCase("INT8", "bigint")]
    [TestCase("BOOL", "boolean")]
    [TestCase("DECIMAL(10,2)", "numeric(10,2)")]
    [TestCase("TIMESTAMPTZ", "timestamp with time zone")]
    public void DomainType_DeclaredWithATypeAlias_DeploysOnACleanDatabase(string declared, string canonical)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"dom_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            Assert.DoesNotThrow(() => RunDomainTypeQuench(cmd, OneDomain(domain, declared)),
                $"a canonical declaration of {declared} must deploy against a database where the domain "
                + "does not exist -- the run created the domain and then refused its own creation");

            Assert.That(LiveBaseType(cmd, domain), Is.EqualTo(canonical),
                "and it must land as the engine's canonical spelling of that alias");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    // The modifier is the half to_regtype alone would lose: it resolves the base and DISCARDS (256),
    // which would wave a real narrowing straight through.
    [Test]
    public void DomainType_NarrowingAnAliasedLength_IsStillRefused()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"domnarrow_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, OneDomain(domain, "VARCHAR(256)"));

            Assert.Throws<Npgsql.PostgresException>(
                () => RunDomainTypeQuench(cmd, OneDomain(domain, "VARCHAR(128)")),
                "resolving the alias must not lose the modifier -- varchar(256) to varchar(128) is a real "
                + "narrowing and is still refused");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    [Test]
    public void DomainType_GenuineBaseTypeChange_IsStillRefused()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"domchange_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, OneDomain(domain, "VARCHAR(256)"));

            Assert.Throws<Npgsql.PostgresException>(
                () => RunDomainTypeQuench(cmd, OneDomain(domain, "integer")),
                "there is no ALTER DOMAIN ... TYPE, so a real base-type change must still be refused by "
                + "name -- resolving aliases must not weaken the guard it lives inside");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    [Test]
    public void DomainType_DeclaredWithATypeThisServerDoesNotKnow_IsRefusedByName()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"dombogus_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, OneDomain(domain, "text"));

            var ex = Assert.Throws<Npgsql.PostgresException>(
                () => RunDomainTypeQuench(cmd, OneDomain(domain, "nosuchtype")),
                "an unrecognised type must be refused as an unrecognised type");
            Assert.That(ex.MessageText, Does.Contain("does not recognise"),
                "and it must say so, rather than reporting it as a base-type change -- that message sends "
                + "the reader looking for a migration when the real problem is a typo");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    // Rule 32: assert the OUTCOME is stable across three passes, authored in natural (aliased) form.
    [Test]
    public void DomainType_DeclaredWithAnAlias_IsStableAcrossThreePasses()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"domstable_{uid}";
        var defs = OneDomain(domain, "VARCHAR(256)");

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, defs);
            var first = LiveDomainOid(cmd, domain);
            RunDomainTypeQuench(cmd, defs);
            RunDomainTypeQuench(cmd, defs);

            Assert.That(LiveDomainOid(cmd, domain), Is.EqualTo(first),
                "three passes over an unchanged declaration must not drop and recreate the domain");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    // ---- check expressions ----------------------------------------------------

    [Test]
    public void DomainType_EditingACheckExpressionUnderAnUnchangedName_Converges()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"domck_{uid}";
        const string check = "has_dot";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, DomainWithCheck(domain, check, "VALUE LIKE '%@%.%'"));
            Assert.That(LiveCheckDef(cmd, domain, check), Does.Contain("%@%.%"), "Setup.");

            RunDomainTypeQuench(cmd, DomainWithCheck(domain, check, "VALUE LIKE '%@%.org'"));

            Assert.That(LiveCheckDef(cmd, domain, check), Does.Contain("%@%.org"),
                "an edited CHECK expression must converge -- the reference promises CHECK, default and "
                + "nullability all converge, and the other two already did");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    // The risk this fix carries is the opposite of the defect. pg_get_constraintdef returns a
    // canonicalised form that never equals the authored text, so a naive comparison would drop and
    // recreate the constraint on EVERY deploy.
    [Test]
    public void DomainType_AnUnchangedCheckExpression_IsStableAcrossThreePasses()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"domckstable_{uid}";
        const string check = "has_dot";
        var defs = DomainWithCheck(domain, check, "VALUE LIKE '%@%.%'");

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, defs);
            var first = LiveCheckOid(cmd, domain, check);
            RunDomainTypeQuench(cmd, defs);
            RunDomainTypeQuench(cmd, defs);

            Assert.That(LiveCheckOid(cmd, domain, check), Is.EqualTo(first),
                "the constraint's oid must be STABLE across three passes, authored in natural form rather "
                + "than pre-canonicalised -- churn here would be a worse defect than the one being fixed");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    [Test]
    public void DomainType_RemovingACheckConstraint_StillDropsIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var domain = $"domckdrop_{uid}";
        const string check = "has_dot";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunDomainTypeQuench(cmd, DomainWithCheck(domain, check, "VALUE LIKE '%@%.%'"));
            RunDomainTypeQuench(cmd, OneDomain(domain, "text"));

            Assert.That(LiveCheckDef(cmd, domain, check), Is.Empty,
                "drop-by-absence was already correct and must not regress -- adding an expression "
                + "comparison must not turn a removal into a modification");
        }
        finally
        {
            DropDomain(cmd, domain);
        }
        conn.Close();
    }

    // ---- fixtures -------------------------------------------------------------

    // Keys are the ones DomainTypeQuench actually reads: Schema, Name, DataType, NotNull, Default,
    // ShouldApplyExpression, CheckConstraints[].Name / .Expression. Note the constraint key is "Name",
    // not "ConstraintName" -- the procedure aliases it to "ConstraintName" internally.
    private static string OneDomain(string name, string dataType) => $$"""
[
  { "Schema": "public", "Name": "{{name}}", "DataType": "{{dataType}}", "NotNull": false }
]
""";

    private static string DomainWithCheck(string name, string check, string expression) => $$"""
[
  {
    "Schema": "public", "Name": "{{name}}", "DataType": "text", "NotNull": false,
    "CheckConstraints": [ { "Name": "{{check}}", "Expression": "{{expression}}" } ]
  }
]
""";

    // ---- drivers and live-state readers ---------------------------------------

    private void RunDomainTypeQuench(IDbCommand cmd, string json)
    {
        cmd.CommandText =
            $@"CALL ""SchemaSmith"".""DomainTypeQuench""('{_productName}', '{json.Replace("'", "''")}', false);";
        cmd.ExecuteNonQuery();
    }

    private void DropDomain(IDbCommand cmd, string name)
    {
        cmd.CommandText = $@"DROP DOMAIN IF EXISTS public.""{name}"" CASCADE;";
        cmd.ExecuteNonQuery();
    }

    private string LiveBaseType(IDbCommand cmd, string name)
    {
        cmd.CommandText = $@"
SELECT FORMAT_TYPE(ty.typbasetype, ty.typtypmod)
  FROM pg_type ty WHERE ty.typtype = 'd' AND ty.typname = '{name}'";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private long LiveDomainOid(IDbCommand cmd, string name)
    {
        cmd.CommandText = $"SELECT oid FROM pg_type WHERE typtype = 'd' AND typname = '{name}'";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string LiveCheckDef(IDbCommand cmd, string domain, string check)
    {
        cmd.CommandText = $@"
SELECT PG_GET_CONSTRAINTDEF(c.oid)
  FROM pg_constraint c JOIN pg_type ty ON ty.oid = c.contypid
 WHERE ty.typname = '{domain}' AND c.conname = '{check}'";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private long LiveCheckOid(IDbCommand cmd, string domain, string check)
    {
        cmd.CommandText = $@"
SELECT c.oid FROM pg_constraint c JOIN pg_type ty ON ty.oid = c.contypid
 WHERE ty.typname = '{domain}' AND c.conname = '{check}'";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
