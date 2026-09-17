// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// #242, the remaining expression surfaces on PostgreSQL: partial-index filters, column defaults and exclude
/// constraints.
/// <para>Measurement first, wiring second. Two surfaces the design listed turned out to be idempotent already,
/// and a release note claiming otherwise would be false — so each case is authored in natural form and asserts
/// the object is left alone. Whichever reddens is a real defect.</para>
/// <para>Identity, not text: an index and a constraint each keep their oid unless they were re-created.</para>
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ExpressionSurfaceMeasurementTests : BaseTableQuenchTests
{
    private sealed class Ctx : IDisposable
    {
        public NpgsqlConnection Conn = null!;
        public IDbCommand Cmd = null!;
        public string Table = null!;

        public void Drop()
        {
            Cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{Table}"" CASCADE;
                                 DELETE FROM ""SchemaSmith"".""ExpressionMap"" WHERE ""ObjectTable"" = '{Table}';";
            Cmd.ExecuteNonQuery();
            Conn.Close();
        }

        public void Dispose() => Conn.Dispose();
    }

    private Ctx NewTable(string columns)
    {
        var table = $"ExprSurf_{Guid.NewGuid():N}"[..16];
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{table}"" CASCADE;
                             CREATE TABLE ""public"".""{table}"" ({columns});";
        cmd.ExecuteNonQuery();
        return new Ctx { Conn = conn, Cmd = cmd, Table = table };
    }

    private static long IndexOid(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT c.oid::bigint FROM pg_class c
                                               WHERE c.relname = '{index}' AND c.relkind = 'i'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string LiveDefault(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT column_default FROM information_schema.columns
                                               WHERE table_schema = 'public' AND table_name = '{table}'
                                                 AND column_name = '{column}'), '')";
        return cmd.ExecuteScalar() as string;
    }

    private static string FilteredIndexJson(Ctx ctx, string filter) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "status", "DataType": "integer", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "IX_{{ctx.Table}}_status", "IndexColumns": "status", "FilterExpression": "{{filter}}" }
            ]
        }
        """;

    private static string DefaultJson(Ctx ctx, string defaultValue) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "tag", "DataType": "text", "Nullable": true, "Default": "{{defaultValue}}" }
            ]
        }
        """;

    // A partial index authored the way a person writes it; PostgreSQL stores the predicate its own way.
    // The column list is authored UNQUOTED to match what the catalog reports: a quoted list churns for its own
    // reason (declared "status" vs live status), which has nothing to do with the predicate and would make
    // this test pass or fail for the wrong reason.
    [Test]
    public void APartialIndexAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = FilteredIndexJson(ctx, @"\""status\"" > 0");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status");
            Assert.That(firstOid, Is.Not.Zero, "setup: the partial index must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status"), Is.EqualTo(firstOid),
                    $"pass {pass}: the partial index was re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A text default: PostgreSQL stores 'none'::text, which the type-cast stripper is meant to reconcile.
    [Test]
    public void ATextDefaultAuthoredInNaturalForm_IsNotReAppliedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            var json = DefaultJson(ctx, @"'none'");
            RunTableQuenchProc(ctx.Cmd, json);
            var first = LiveDefault(ctx.Cmd, ctx.Table, "tag");
            Assert.That(first, Is.Not.Empty, "setup: the default must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(LiveDefault(ctx.Cmd, ctx.Table, "tag"), Is.EqualTo(first),
                    $"pass {pass}: the default moved for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A function default, which PostgreSQL reframes more than a literal.
    [Test]
    public void AFunctionDefaultAuthoredInNaturalForm_IsNotReAppliedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            var json = DefaultJson(ctx, @"upper('abc')");
            RunTableQuenchProc(ctx.Cmd, json);
            var first = LiveDefault(ctx.Cmd, ctx.Table, "tag");
            Assert.That(first, Is.Not.Empty, "setup: the default must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(LiveDefault(ctx.Cmd, ctx.Table, "tag"), Is.EqualTo(first),
                    $"pass {pass}: the function default moved for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    private static string ExcludeJson(Ctx ctx, string filter) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "status", "DataType": "integer", "Nullable": true }
            ],
            "ExcludeConstraints": [
                {
                    "Name": "EX_{{ctx.Table}}_id",
                    "AccessMethod": "btree",
                    "ExcludeColumns": [ { "Column": "id", "Operator": "=" } ],
                    "FilterExpression": "{{filter}}"
                }
            ]
        }
        """;

    private static long ConstraintOid(IDbCommand cmd, string name)
    {
        cmd.CommandText = $@"SELECT COALESCE((SELECT con.oid::bigint FROM pg_constraint con
                                               WHERE con.conname = '{name}'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Exclude constraints carry a predicate too, and the design lists them as a churn surface.
    [Test]
    public void AnExcludeConstraintAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = ExcludeJson(ctx, "status > 0");
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = ConstraintOid(ctx.Cmd, $"EX_{ctx.Table}_id");
            Assert.That(firstOid, Is.Not.Zero, "setup: the exclude constraint must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json);
                Assert.That(ConstraintOid(ctx.Cmd, $"EX_{ctx.Table}_id"), Is.EqualTo(firstOid),
                    $"pass {pass}: the exclude constraint was re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // The index-only path (Template.IndexOnlyTableQuenches) has its own index comparison in IndexOnlyQuench,
    // separate from ModifiedTableQuench, so wiring the full quench did not reach it.
    [Test]
    public void APartialIndexInIndexOnlyMode_IsNotReCreatedOnEveryDeploy()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = FilteredIndexJson(ctx, @"\""status\"" > 0");
            RunTableQuenchProc(ctx.Cmd, json, indexOnly: true);
            var firstOid = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status");
            Assert.That(firstOid, Is.Not.Zero, "setup: the partial index must exist after the first index-only deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json, indexOnly: true);
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status"), Is.EqualTo(firstOid),
                    $"pass {pass}: index-only mode re-created the partial index for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A rename pairs the declared index with the live one by columns AND predicate. When PostgreSQL has rewritten
    // the predicate, that pairing never matched, and the rename fell through to dropping the old index and
    // building the new one -- a full index build instead of a catalog rename.
    [Test]
    public void RenamingAPartialIndex_IsARename_NotADropAndRebuild()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            RunTableQuenchProc(ctx.Cmd, FilteredIndexJson(ctx, @"\""status\"" > 0"));
            var oidBefore = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status");
            Assert.That(oidBefore, Is.Not.Zero, "setup: the partial index must exist");

            var renamed = FilteredIndexJson(ctx, @"\""status\"" > 0").Replace($"IX_{ctx.Table}_status", $"IX_{ctx.Table}_renamed");
            RunTableQuenchProc(ctx.Cmd, renamed);

            Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_renamed"), Is.EqualTo(oidBefore),
                "a rename must keep the index (same oid under the new name), not drop it and build a new one");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // With DropUnknownIndexes on, an index that was just renamed is still in the pre-rename snapshot under its OLD
    // name and absent from the package. It must not be selected as "unknown" and dropped (or logged as dropped).
    // Asserted on what the server reports: the renamed index keeps its oid, and no notice claims a drop.
    [TestCase(false)]
    [TestCase(true)]
    public void RenamingAnIndex_WithDropUnknownIndexes_DoesNotDropIt(bool indexOnly)
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        var notices = new System.Collections.Generic.List<string>();
        ctx.Conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        try
        {
            var json = FilteredIndexJson(ctx, "").Replace(@", ""FilterExpression"": """"", "");
            RunTableQuenchProc(ctx.Cmd, json, indexOnly: indexOnly);
            var oidBefore = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_status");
            Assert.That(oidBefore, Is.Not.Zero, "setup");

            notices.Clear();
            var renamed = json.Replace($"IX_{ctx.Table}_status", $"IX_{ctx.Table}_renamed");
            ctx.Cmd.CommandText = indexOnly
                ? $@"CALL ""SchemaSmith"".""IndexOnlyQuench""(p_ProductName := '{_productName}', p_TableDefinitions := '{renamed.Replace("'", "''")}', p_DropUnknownIndexes := true);"
                : $@"CALL ""SchemaSmith"".""TableQuench""(p_ProductName := '{_productName}', p_TableDefinitions := '{renamed.Replace("'", "''")}', p_WhatIf := false, p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := true)";
            ctx.Cmd.ExecuteNonQuery();

            Assert.Multiple(() =>
            {
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_renamed"), Is.EqualTo(oidBefore),
                    $"the index must survive the rename (indexOnly={indexOnly})");
                Assert.That(notices.FindAll(n => n.Contains("Dropping") && n.Contains(ctx.Table)), Is.Empty,
                    $"no drop may be logged for a renamed index (indexOnly={indexOnly}): {string.Join(" | ", notices.FindAll(n => n.Contains("Dropping") || n.Contains("Renaming")))}");
            });
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    private static long StatisticsOid(IDbCommand cmd, string name)
    {
        cmd.CommandText = $"SELECT COALESCE((SELECT oid::bigint FROM pg_statistic_ext WHERE stxname = '{name}'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string StatisticsJson(Ctx ctx, string name, string columns, string kind = null) => $$"""
        {
            "Schema": "public",
            "Name": "{{ctx.Table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "tag", "DataType": "text", "Nullable": true }
            ],
            "Statistics": [
                { "Name": "{{name}}", "StatisticsColumns": "{{columns}}"{{(kind == null ? "" : ", \"Kind\": \"" + kind + "\"")}} }
            ]
        }
        """;

    private int ServerMajor(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Extended statistics, measured on PostgreSQL 17 before any fix. The catalog keeps neither the authored order
    // nor the authored kind list: stxkeys is the column set in attnum order with expressions held apart, and stxkind
    // gains an 'e' for any expression. Every case below was dropped and re-created on every deploy.
    [TestCase("lower(tag), id", null, false, true, TestName = "ExpressionThenColumn")]
    [TestCase("lower(tag), id", null, true, true, TestName = "ExpressionThenColumn_IndexOnly")]
    [TestCase("LOWER(tag), id", null, false, true, TestName = "ExpressionRewrittenByTheEngine")]
    [TestCase("(id + 1)", null, false, true, TestName = "SingleExpression")]
    [TestCase("(id+1), (lower(tag))", null, false, true, TestName = "TwoExpressionsNaturalForm")]
    [TestCase("tag, id", null, false, false, TestName = "ColumnsNotInAttnumOrder")]
    [TestCase("\\\"tag\\\", id", null, false, false, TestName = "QuotedColumn")]
    [TestCase("id, tag", "mcv, ndistinct", false, false, TestName = "LowerCaseKindsInAnyOrder")]
    [TestCase("id, tag", "ndistinct,dependencies,mcv", true, false, TestName = "AllKindsSpelledOut_IndexOnly")]
    public void AStatisticAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy(string columns, string kind, bool indexOnly, bool needs14)
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            if (needs14 && ServerMajor(ctx.Cmd) < 14)
                Assert.Ignore("Expression statistics are PostgreSQL 14+.");

            var name = $"st_{ctx.Table}_n".ToLowerInvariant();
            var json = StatisticsJson(ctx, name, columns, kind);
            RunTableQuenchProc(ctx.Cmd, json, indexOnly: indexOnly);
            var firstOid = StatisticsOid(ctx.Cmd, name);
            Assert.That(firstOid, Is.Not.Zero, "setup: the statistics object must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json, indexOnly: indexOnly);
                Assert.That(StatisticsOid(ctx.Cmd, name), Is.EqualTo(firstOid),
                    $"pass {pass}: the statistic was re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // The normalisation must not make different definitions equal: a real change converges, on both paths. Index-
    // only mode never detected a changed statistic at all before -- it only created missing ones.
    [TestCase("id, tag", "ndistinct", "id, tag", "mcv", false, "(mcv) ON id, tag FROM")]
    [TestCase("id, tag", null, "id, (id + 1)", null, false, "ON id, (id + 1) FROM")]
    [TestCase("id, tag", null, "id, (id + 1)", null, true, "ON id, (id + 1) FROM")]
    [TestCase("id, tag", "ndistinct", "id, tag", "mcv", true, "(mcv) ON id, tag FROM")]
    [TestCase("lower(tag), id", null, "upper(tag), id", null, true, "ON id, upper(tag) FROM")]
    public void ChangingAStatistic_IsApplied(string columnsBefore, string kindBefore, string columnsAfter, string kindAfter, bool indexOnly, string expectedDefinition)
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            if ((columnsBefore + columnsAfter).Contains('(') && ServerMajor(ctx.Cmd) < 14)
                Assert.Ignore("Expression statistics are PostgreSQL 14+.");

            var name = $"st_{ctx.Table}_c".ToLowerInvariant();
            RunTableQuenchProc(ctx.Cmd, StatisticsJson(ctx, name, columnsBefore, kindBefore), indexOnly: indexOnly);
            RunTableQuenchProc(ctx.Cmd, StatisticsJson(ctx, name, columnsAfter, kindAfter), indexOnly: indexOnly);

            ctx.Cmd.CommandText = $"SELECT pg_get_statisticsobjdef(oid) FROM pg_statistic_ext WHERE stxname = '{name}'";
            var def = ctx.Cmd.ExecuteScalar() as string;
            Assert.That(def, Does.Contain(expectedDefinition), $"indexOnly={indexOnly}: the change must be applied: {def}");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // SchemaTongs extracted an expression statistic with Kind "NDISTINCT,DEPENDENCIES,MCV,EXPRESSIONS" (or just
    // "EXPRESSIONS" for a single expression). CREATE STATISTICS rejects EXPRESSIONS as a kind, so an extracted
    // package could not be deployed. Packages already extracted that way must deploy, and stay put.
    [TestCase("lower(tag), id", "NDISTINCT,DEPENDENCIES,MCV,EXPRESSIONS")]
    [TestCase("(id + 1)", "EXPRESSIONS")]
    public void AnExtractedExpressionsKind_Deploys_AndIsStable(string columns, string kind)
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""tag"" text NULL");
        try
        {
            if (ServerMajor(ctx.Cmd) < 14)
                Assert.Ignore("Expression statistics are PostgreSQL 14+.");

            var name = $"st_{ctx.Table}_x".ToLowerInvariant();
            var json = StatisticsJson(ctx, name, columns, kind);
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = StatisticsOid(ctx.Cmd, name);
            Assert.That(firstOid, Is.Not.Zero, "a package carrying the EXPRESSIONS kind must deploy");

            RunTableQuenchProc(ctx.Cmd, json);
            Assert.That(StatisticsOid(ctx.Cmd, name), Is.EqualTo(firstOid), "and must not be re-created on the next deploy");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // Found while isolating the partial-index predicate: an index authoring its key columns QUOTED ("status")
    // never matched the snapshot, which strips PG_GET_INDEXDEF's quotes, so it was dropped and rebuilt on every
    // deploy for a reason unrelated to any expression. Quoting is how SchemaTongs itself writes them.
    // The same snapshot also drops the sort options PostgreSQL treats as defaults, so an explicit ASC, a
    // lower-case desc, or a spelled-out default NULLS placement churned the same way.
    [TestCase(@"\""status\""", null, false)]
    [TestCase(@"\""status\""", null, true)]
    [TestCase(@"status", @"\""id\""", false)]
    [TestCase(@"status", @"\""id\""", true)]
    [TestCase(@"status, id", @"id,  status", false)]
    [TestCase(@"\""status\"" DESC", null, false)]
    [TestCase(@"status asc", null, false)]
    [TestCase(@"status desc", null, true)]
    [TestCase(@"status DESC NULLS FIRST", null, false)]
    [TestCase(@"status ASC NULLS LAST", null, false)]
    [TestCase(@"status NULLS FIRST", null, false)]
    public void AnIndexWithQuotedColumns_IsNotReCreatedOnEveryDeploy(string keyColumns, string includeColumns, bool indexOnly)
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = $$"""
                {
                    "Schema": "public",
                    "Name": "{{ctx.Table}}",
                    "Columns": [
                        { "Name": "id", "DataType": "integer", "Nullable": false },
                        { "Name": "status", "DataType": "integer", "Nullable": true }
                    ],
                    "Indexes": [
                        { "Name": "IX_{{ctx.Table}}_q", "IndexColumns": "{{keyColumns}}"{{(includeColumns == null ? "" : ", \"IncludeColumns\": \"" + includeColumns + "\"")}} }
                    ]
                }
                """;
            RunTableQuenchProc(ctx.Cmd, json, indexOnly: indexOnly);
            var firstOid = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_q");
            Assert.That(firstOid, Is.Not.Zero, "setup: the index must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(ctx.Cmd, json, indexOnly: indexOnly);
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_q"), Is.EqualTo(firstOid),
                    $"pass {pass}: the index was re-created for an unchanged declaration");
            }
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // The fix normalises quotes for COMPARISON only. A mixed-case column must keep its quotes when the index is
    // created (unquoted, PostgreSQL would fold "Status" to status and the CREATE would fail), and must still be
    // idempotent.
    [Test]
    public void AnIndexOnAMixedCaseColumn_IsCreated_AndIsIdempotent()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""Status"" integer NULL");
        try
        {
            var json = $$"""
                {
                    "Schema": "public",
                    "Name": "{{ctx.Table}}",
                    "Columns": [
                        { "Name": "id", "DataType": "integer", "Nullable": false },
                        { "Name": "Status", "DataType": "integer", "Nullable": true }
                    ],
                    "Indexes": [ { "Name": "IX_{{ctx.Table}}_mixed", "IndexColumns": "\"Status\"" } ]
                }
                """;
            RunTableQuenchProc(ctx.Cmd, json);
            var firstOid = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_mixed");
            Assert.That(firstOid, Is.Not.Zero, "the index on a mixed-case column must be created");

            RunTableQuenchProc(ctx.Cmd, json);
            Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_mixed"), Is.EqualTo(firstOid), "and left alone on the next deploy");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // A real column change must still be detected -- the comparison normaliser must not make different columns equal.
    [Test]
    public void ChangingAnIndexsColumns_IsStillApplied()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            string Json(string cols) => $$"""
                { "Schema": "public", "Name": "{{ctx.Table}}",
                  "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "status", "DataType": "integer", "Nullable": true } ],
                  "Indexes": [ { "Name": "IX_{{ctx.Table}}_chg", "IndexColumns": "{{cols}}" } ] }
                """;
            RunTableQuenchProc(ctx.Cmd, Json(@"\""status\""") );
            RunTableQuenchProc(ctx.Cmd, Json(@"\""id\""") );

            ctx.Cmd.CommandText = $"SELECT pg_get_indexdef(to_regclass('public.\"IX_{ctx.Table}_chg\"'))";
            Assert.That(ctx.Cmd.ExecuteScalar() as string, Does.Contain("(id)"), "the index must now be on id");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // Found while wiring the column normaliser into IndexOnlyQuench: its modified-index predicate closed a
    // parenthesis after AccessMethod, leaving the NullsNotDistinct, Deferrable, InitiallyDeferred and
    // StorageParameters comparisons outside the name match. One DEFERRABLE unique constraint on a table then made
    // every OTHER index on it look modified, and index-only mode rebuilt them all on every deploy.
    [Test]
    public void ADeferrableConstraint_DoesNotMakeItsNeighboursLookModified_InIndexOnlyMode()
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            var json = $$"""
                {
                    "Schema": "public",
                    "Name": "{{ctx.Table}}",
                    "Columns": [
                        { "Name": "id", "DataType": "integer", "Nullable": false },
                        { "Name": "status", "DataType": "integer", "Nullable": true }
                    ],
                    "Indexes": [
                        { "Name": "IX_{{ctx.Table}}_plain", "IndexColumns": "status" },
                        { "Name": "UQ_{{ctx.Table}}_id", "IndexColumns": "id", "Unique": true, "UniqueConstraint": true, "Deferrable": true }
                    ]
                }
                """;
            RunTableQuenchProc(ctx.Cmd, json, indexOnly: true);
            var plainOid = IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_plain");
            var uniqueOid = IndexOid(ctx.Cmd, ctx.Table, $"UQ_{ctx.Table}_id");
            Assert.That(plainOid, Is.Not.Zero, "setup: the plain index");
            Assert.That(uniqueOid, Is.Not.Zero, "setup: the deferrable unique constraint");

            RunTableQuenchProc(ctx.Cmd, json, indexOnly: true);
            Assert.Multiple(() =>
            {
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"IX_{ctx.Table}_plain"), Is.EqualTo(plainOid),
                    "the plain index was rebuilt because a different index on the table is deferrable");
                Assert.That(IndexOid(ctx.Cmd, ctx.Table, $"UQ_{ctx.Table}_id"), Is.EqualTo(uniqueOid),
                    "the deferrable constraint was rebuilt because a different index on the table is not");
            });
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }

    // And the full path never compared DEFERRABLE / INITIALLY DEFERRED on a key at all, so making one deferrable
    // was silently ignored -- the index-only path did detect it. Both paths must converge it.
    [TestCase(false)]
    [TestCase(true)]
    public void MakingAUniqueConstraintDeferrable_IsApplied(bool indexOnly)
    {
        var ctx = NewTable(@"""id"" integer NOT NULL, ""status"" integer NULL");
        try
        {
            string Json(bool deferrable) => $$"""
                { "Schema": "public", "Name": "{{ctx.Table}}",
                  "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "status", "DataType": "integer", "Nullable": true } ],
                  "Indexes": [ { "Name": "UQ_{{ctx.Table}}_id", "IndexColumns": "id", "Unique": true, "UniqueConstraint": true, "Deferrable": {{(deferrable ? "true" : "false")}}, "InitiallyDeferred": {{(deferrable ? "true" : "false")}} } ] }
                """;
            RunTableQuenchProc(ctx.Cmd, Json(false), indexOnly: indexOnly);
            RunTableQuenchProc(ctx.Cmd, Json(true), indexOnly: indexOnly);

            ctx.Cmd.CommandText = $"SELECT condeferrable::text || '|' || condeferred::text FROM pg_constraint WHERE conname = 'UQ_{ctx.Table}_id'";
            Assert.That(ctx.Cmd.ExecuteScalar() as string, Is.EqualTo("true|true"),
                $"the constraint must now be DEFERRABLE INITIALLY DEFERRED (indexOnly={indexOnly})");
        }
        finally { ctx.Drop(); ctx.Dispose(); }
    }
}
