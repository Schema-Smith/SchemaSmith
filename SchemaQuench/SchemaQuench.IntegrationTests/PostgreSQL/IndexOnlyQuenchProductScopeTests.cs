// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Index-only quench on PostgreSQL, exercised through the argument list SchemaQuench actually emits.
///
/// <para>The emitted CALL omitted <c>p_ProductName</c>, so index-only mode failed at exit 2 on every
/// PostgreSQL deploy. The argument is not decorative: <c>IndexOnlyQuench.sql</c> joins on it at :199 and
/// :232 to scope drop-by-absence to indexes THIS product owns. Because the argument never arrived, that
/// ownership path has never executed on PostgreSQL -- so it is asserted here rather than assumed.</para>
///
/// <para>The emitted-text contract lives in <c>DatabaseQuenchIndexOnlyCallTests</c>. This fixture proves
/// the procedure does the right thing once it is called correctly.</para>
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class IndexOnlyQuenchProductScopeTests : BaseTableQuenchTests
{
    [Test]
    public void IndexOnlyQuench_OnATableThePackageDoesNotOwn_AddsTheDeclaredIndex()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"IdxOnlyProduct_{uid}";
        var table = $"idxonly_{uid}";
        var index = $"ix_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        // The base class builds its connection against 'postgres'; the SchemaSmith schema and the test
        // objects live in the main test database, so every PostgreSQL fixture here switches after open.
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            CreateVendorTable(cmd, table);

            Assert.DoesNotThrow(() => RunIndexOnly(cmd, Defs(table, index), product),
                "a documented four-engine feature must work on all four -- the CALL omitted the required "
                + "p_ProductName, so no overload matched and PostgreSQL reported it as 42883, "
                + "'procedure does not exist', for a procedure that was installed and correct");

            Assert.Multiple(() =>
            {
                Assert.That(IndexExists(cmd, index), Is.True, "the declared index must land");
                Assert.That(ColumnNames(cmd, table), Is.EqualTo("Id,Val"),
                    "and the vendor's table is untouched -- managing indexes on tables you do not own "
                    + "is the entire point of the mode");
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // The ownership join p_ProductName feeds. Never executed on PostgreSQL before this fix.
    [Test]
    public void IndexOnlyQuench_DropsAnOwnedIndexRemovedFromTheProduct()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"IdxOwnProduct_{uid}";
        var table = $"idxown_{uid}";
        var index = $"ixown_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        // The base class builds its connection against 'postgres'; the SchemaSmith schema and the test
        // objects live in the main test database, so every PostgreSQL fixture here switches after open.
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            CreateVendorTable(cmd, table);
            RunIndexOnly(cmd, Defs(table, index), product);
            Assert.That(IndexExists(cmd, index), Is.True, "Setup: the index deploys and is owned.");

            // Same table, no indexes declared.
            RunIndexOnly(cmd, DefsNoIndexes(table), product, dropRemoved: true);

            Assert.That(IndexExists(cmd, index), Is.False,
                "an index this product owns, removed from the declaration, drops by absence -- that "
                + "join is scoped by p_ProductName, so it could never have fired before the argument "
                + "was passed");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // Ownership must SCOPE the drop, not just enable it: another product's index is not this
    // product's to remove.
    [Test]
    public void IndexOnlyQuench_LeavesAnIndexOwnedByAnotherProductAlone()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var mine = $"IdxMineProduct_{uid}";
        var theirs = $"IdxTheirsProduct_{uid}";
        var table = $"idxscope_{uid}";
        var myIndex = $"ixmine_{uid}";
        var theirIndex = $"ixtheirs_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        // The base class builds its connection against 'postgres'; the SchemaSmith schema and the test
        // objects live in the main test database, so every PostgreSQL fixture here switches after open.
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            CreateVendorTable(cmd, table);
            RunIndexOnly(cmd, Defs(table, myIndex), mine);
            RunIndexOnly(cmd, Defs(table, theirIndex), theirs);

            // My product now declares nothing. Theirs is untouched and still declared by them.
            RunIndexOnly(cmd, DefsNoIndexes(table), mine, dropRemoved: true);

            Assert.Multiple(() =>
            {
                Assert.That(IndexExists(cmd, myIndex), Is.False, "my own index drops");
                Assert.That(IndexExists(cmd, theirIndex), Is.True,
                    "another product's index survives -- p_ProductName is what makes drop-by-absence a "
                    + "per-product decision instead of a free-for-all on a shared table");
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // ---- fixtures -------------------------------------------------------------

    private static string Defs(string table, string index) => $$"""
[
  {
    "Schema": "public",
    "Name": "{{table}}",
    "Indexes": [ { "Name": "{{index}}", "IndexColumns": "\"Val\"" } ]
  }
]
""";

    private static string DefsNoIndexes(string table) => $$"""
[
  { "Schema": "public", "Name": "{{table}}", "Indexes": [] }
]
""";

    // ---- drivers and live-state readers ---------------------------------------

    /// <summary>
    /// Mirrors the argument list DatabaseQuench emits for the PostgreSQL index-only branch, including
    /// p_ProductName. Kept explicit rather than reusing the base helper, which hard-codes a different
    /// flag set -- and note that the base helper has always passed p_ProductName, which is exactly why
    /// integration coverage never caught the omission.
    /// </summary>
    private void RunIndexOnly(IDbCommand cmd, string json, string product, bool dropRemoved = false)
    {
        var defs = json.Replace("'", "''");
        cmd.CommandText = $@"
CALL ""SchemaSmith"".""IndexOnlyQuench""(p_ProductName := '{product}', p_TableDefinitions := '{defs}', p_DropUnknownIndexes := false, p_DropIndexesRemovedFromProduct := {(dropRemoved ? "true" : "false")}, p_WhatIf := false, p_UpdateFillFactor := true, p_CaptureWouldDrop := false);
CALL ""SchemaSmith"".""FixupIndexOwnership""(p_ProductName := '{product}');
";
        cmd.ExecuteNonQuery();
    }

    private void CreateVendorTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"CREATE TABLE public.""{table}"" (""Id"" int NOT NULL, ""Val"" text);";
        cmd.ExecuteNonQuery();
    }

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"DROP TABLE IF EXISTS public.""{table}"" CASCADE;";
        cmd.ExecuteNonQuery();
    }

    private bool IndexExists(IDbCommand cmd, string index)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{index}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private string ColumnNames(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT STRING_AGG(column_name, ',' ORDER BY ordinal_position)
  FROM information_schema.columns
 WHERE table_schema = 'public' AND table_name = '{table}'";
        return cmd.ExecuteScalar() as string ?? "";
    }
}
