// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Perpetual-churn guards: objects that were dropped and recreated on EVERY deploy because the
/// comparison could never report them equal.
///
/// <para><b>Asserted at the outcome, per Rule 32.</b> Each test authors the object in its NATURAL
/// form, deploys three times, and requires the catalog <c>oid</c> to be unchanged. Asserting that
/// some normalised string matches is what let these live: the existing suite authors expressions
/// already canonicalised, so it compares the tool against its own output rather than against what a
/// user writes. An oid that moves is a drop and recreate, whatever the strings did.</para>
///
/// <para>Three passes rather than two: the first deploy creates, the second is where a broken
/// comparison first churns, and the third catches a comparison that merely alternates.</para>
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class ExpressionChurnStabilityTests : BaseTableQuenchTests
{
    // Defect 1: the compare stripped CHECK's prefix with a regex requiring DOUBLE parens, but
    // pg_get_constraintdef renders SINGLE parens for a bare boolean column and for a top-level
    // function call. Those two shapes could never compare equal, so they churned forever, while a
    // relational expression -- which does render double -- was fine. Extraction had always used the
    // balanced-paren helper, so the two disagreed with each other.
    //
    // A TOP-LEVEL FUNCTION CALL IS DELIBERATELY NOT COVERED HERE, and the omission is the finding.
    // CHECK (starts_with(tag, 'a')) also renders single-paren, but PostgreSQL additionally rewrites
    // the literal: pg_get_constraintdef returns starts_with(tag, 'a'::text). Paren handling cannot
    // reconcile an added cast, so that shape still churns -- for a SECOND, independent reason the
    // roadmap entry folded into this one. Measured before and after the fix: unchanged either way.
    // Filed separately rather than covered by a test authored as 'a'::text, because a fixture
    // written in the engine's canonical form proves the tool agrees with itself and nothing more
    // -- which is precisely how these defects survived.
    [TestCase("flag", TestName = "CheckConstraint_BareBooleanColumn_DoesNotChurn")]
    [TestCase("length(tag) > 0", TestName = "CheckConstraint_RelationalExpression_DoesNotChurn")]
    public void CheckConstraint_AuthoredNaturally_IsStableAcrossThreePasses(string expression)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"ChkChurn_{uid}";
        var table = $"chk_churn_{uid}";
        var defs = TableWithCheck(table, "ck_churn", expression);

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            var first = ConstraintOid(cmd, table, "ck_churn");
            Assert.That(first, Is.GreaterThan(0), "Setup: the constraint must deploy.");

            RunTableQuenchProc(cmd, defs, productName: product);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ConstraintOid(cmd, table, "ck_churn"), Is.EqualTo(first),
                "the constraint was dropped and recreated on an unchanged declaration -- the compare "
                + "could not report it equal, so it churned on every deploy");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // Defect 3 (#285): a PRIMARY KEY is unique in the catalog whether or not the package says so, so
    // a naturally-authored PK -- PrimaryKey: true, no Unique -- failed the rename join and fell
    // through to drop and recreate instead of being renamed in place.
    [Test]
    public void RenamingANaturallyAuthoredPrimaryKey_RenamesRatherThanRecreates()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PkRename_{uid}";
        var table = $"pk_rename_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, TableWithPrimaryKey(table, "pk_before"), productName: product);
            var before = IndexOid(cmd, "pk_before");
            Assert.That(before, Is.GreaterThan(0), "Setup: the primary key must deploy.");

            RunTableQuenchProc(cmd, TableWithPrimaryKey(table, "pk_after"), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(IndexOid(cmd, "pk_after"), Is.EqualTo(before),
                    "a rename must RENAME -- the same oid under the new name. A different oid means the "
                    + "key was dropped and rebuilt, which on a large table is an outage the package "
                    + "never asked for");
                Assert.That(IndexOid(cmd, "pk_before"), Is.EqualTo(0), "the old name must be gone");
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // Defect 2: the compare snapshot built IndexColumns by joining pg_attribute on attnum = element.
    // An EXPRESSION key has indkey element 0, which matches no attribute, so the key was dropped from
    // the snapshot entirely while the authored side carried lower(name) -- never equal, so every
    // expression index was dropped and recreated on every deploy.
    //
    // Authored as EXTRACTION emits it -- bare, no wrapping parens -- because that is the canonical
    // form by definition: a package refreshed by SchemaTongs contains exactly this, so this is the
    // text a real re-deploy compares.
    [TestCase("lower(name)", TestName = "ExpressionIndex_SingleExpressionKey_DoesNotChurn")]
    [TestCase("tag,lower(name)", TestName = "ExpressionIndex_ColumnThenExpressionKey_DoesNotChurn")]
    public void ExpressionIndex_AuthoredAsExtractionEmitsIt_IsStableAcrossThreePasses(string indexColumns)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"IxChurn_{uid}";
        var table = $"ix_churn_{uid}";
        var index = $"ix_expr_{uid}";
        var defs = TableWithIndex(table, index, indexColumns);

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            var first = IndexOid(cmd, index);
            Assert.That(first, Is.GreaterThan(0), "Setup: the expression index must deploy.");

            RunTableQuenchProc(cmd, defs, productName: product);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(IndexOid(cmd, index), Is.EqualTo(first),
                "the expression index was dropped and recreated on an unchanged declaration -- the "
                + "compare snapshot could not see its expression key, so it churned on every deploy");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // The other half of defect 2, and the sharper half. For a PURE expression index the snapshot
    // returned NULL rather than a wrong value, so the compare saw no difference at all -- which means
    // it did not churn, it failed to notice. An expression that genuinely CHANGED was therefore left
    // deployed as it was. Stopping the churn and restoring detection are the same fix; this pins the
    // half that a stability test cannot see.
    [Test]
    public void ChangingAnIndexExpression_IsDetectedAndApplied()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"IxChange_{uid}";
        var table = $"ix_change_{uid}";
        var index = $"ix_chg_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, TableWithIndex(table, index, "lower(name)"), productName: product);
            Assert.That(LiveIndexDef(cmd, index), Does.Contain("lower"), "Setup: the index must deploy.");

            RunTableQuenchProc(cmd, TableWithIndex(table, index, "upper(name)"), productName: product);

            Assert.That(LiveIndexDef(cmd, index), Does.Contain("upper"),
                "the declared expression changed and the deployed index still uses the old one -- the "
                + "snapshot could not see the expression key, so the compare had nothing to disagree "
                + "with and silently left it alone");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // ---- fixtures -------------------------------------------------------------

    // Authored the way a user writes it: the CHECK carries no defensive outer parens, and the
    // primary key declares PrimaryKey without also declaring Unique.
    private static string TableWithCheck(string table, string constraint, string expression) => $$"""
[
  {
    "Schema": "public",
    "Name": "{{table}}",
    "Columns": [
      { "Name": "id", "DataType": "INT4", "Nullable": false },
      { "Name": "flag", "DataType": "BOOLEAN", "Nullable": true },
      { "Name": "tag", "DataType": "TEXT", "Nullable": true }
    ],
    "CheckConstraints": [ { "Name": "{{constraint}}", "Expression": "{{expression}}" } ]
  }
]
""";

    private static string TableWithPrimaryKey(string table, string pkName) => $$"""
[
  {
    "Schema": "public",
    "Name": "{{table}}",
    "Columns": [
      { "Name": "id", "DataType": "INT4", "Nullable": false },
      { "Name": "tag", "DataType": "TEXT", "Nullable": true }
    ],
    "Indexes": [ { "Name": "{{pkName}}", "PrimaryKey": true, "IndexColumns": "id" } ]
  }
]
""";

    private static string TableWithIndex(string table, string index, string indexColumns) => $$"""
[
  {
    "Schema": "public",
    "Name": "{{table}}",
    "Columns": [
      { "Name": "id", "DataType": "INT4", "Nullable": false },
      { "Name": "name", "DataType": "TEXT", "Nullable": true },
      { "Name": "tag", "DataType": "TEXT", "Nullable": true }
    ],
    "Indexes": [ { "Name": "{{index}}", "IndexColumns": "{{indexColumns}}" } ]
  }
]
""";

    // ---- live-state readers ---------------------------------------------------

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"DROP TABLE IF EXISTS public.""{table}"" CASCADE;";
        cmd.ExecuteNonQuery();
    }

    // 0 when absent, so a caller can assert either identity or disappearance without a null dance.
    private long ConstraintOid(IDbCommand cmd, string table, string constraint)
    {
        cmd.CommandText = $@"
SELECT COALESCE((SELECT c.oid FROM pg_constraint c
                   JOIN pg_class t ON t.oid = c.conrelid
                  WHERE t.relname = '{table}' AND c.conname = '{constraint}'), 0)::bigint";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string LiveIndexDef(IDbCommand cmd, string index)
    {
        cmd.CommandText = $"SELECT COALESCE(pg_get_indexdef(oid), '') FROM pg_class WHERE relname = '{index}' AND relkind = 'i'";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private long IndexOid(IDbCommand cmd, string index)
    {
        cmd.CommandText =
            $"SELECT COALESCE((SELECT oid FROM pg_class WHERE relname = '{index}' AND relkind = 'i'), 0)::bigint";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
