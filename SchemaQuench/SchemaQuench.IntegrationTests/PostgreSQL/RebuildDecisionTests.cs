// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// The rebuild DECISION point on PostgreSQL: which tables ModifiedTableQuench elects for a rebuild, and
/// -- far more important -- which it must not. RebuildTableTests covers the rebuild engine itself; this
/// fixture covers the one thing that decides whether that engine is ever pointed at a user's table.
///
/// Everything here runs the whole "SchemaSmith"."TableQuench" path (parse -> missing -> modified -> indexes
/// -> foreign keys) rather than calling RebuildTable directly, because the decision only exists inside that
/// path and a test that reached around it would prove nothing about it.
///
/// ANTI-VACUITY. "Nothing happened" is the passing state for half of these tests, so every one of them has
/// to be able to tell "nothing happened" apart from "the test asserted nothing". Two devices do that:
///   * a LIVE-ONLY MARKER INDEX the package never declares. RebuildTable drops the old table whole and
///     re-creates nothing but columns, and no downstream pass re-adds an index the package does not carry,
///     so the marker surviving is proof the table was NOT replaced and the marker being gone is proof it was.
///   * the relation's OID, captured before and compared after -- a rebuild swaps in a different relation.
/// And every test asserts its preconditions, so a setup that silently failed to produce the drift under
/// test cannot read as a pass.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class RebuildDecisionTests : BaseTableQuenchTests
{
    private const string MarkerIndex = "ix_rebuild_decision_marker";
    private const string TableName = "RebuildDecision";

    // ---- harness ------------------------------------------------------------

    private IDbConnection OpenMainDb()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    private static string NewSchema(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    /// <summary>
    /// Runs the real TableQuench with the resolved upper-tier policy attached. Passing no mode leaves the
    /// procedure's own defaults in place, which is exactly the shape every caller that predates
    /// RebuildPolicy produces -- so the "no policy anywhere" test really is the untouched code path.
    /// </summary>
    private void Quench(IDbCommand cmd, string json, string mode = null, int? threshold = null, bool whatIf = false)
    {
        var policy = mode == null
            ? ""
            : $", p_RebuildPolicyMode := {Lit(mode)}, p_RebuildPolicyThreshold := {(threshold.HasValue ? threshold.Value.ToString() : "NULL")}";
        Exec(cmd, $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := {Lit(_productName)}, "
                  + $"p_TableDefinitions := {Lit(json)}, p_WhatIf := {(whatIf ? "TRUE" : "FALSE")}, "
                  + $"p_DropUnknownIndexes := FALSE, p_DropTablesRemovedFromProduct := FALSE{policy});");
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        return TransientRetry.Run(() => cmd.ExecuteScalar());
    }

    private static long Long(IDbCommand cmd, string sql)
    {
        var v = Scalar(cmd, sql);
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt64(v);
    }

    private static int Int(IDbCommand cmd, string sql) => (int)Long(cmd, sql);

    private static string Str(IDbCommand cmd, string sql)
    {
        var v = Scalar(cmd, sql);
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    /// <summary>-1 when the relation does not exist, so "no such object" is an unambiguous value rather
    /// than a null/DBNull ambiguity in the assertion.</summary>
    private static long Oid(IDbCommand cmd, string schema, string name)
        => Long(cmd, $"SELECT COALESCE(to_regclass('\"{schema}\".\"{name}\"')::oid::BIGINT, -1)");

    private static int MarkerCount(IDbCommand cmd, string schema)
        => Int(cmd, "SELECT COUNT(*) FROM pg_indexes "
                    + $"WHERE schemaname = {Lit(schema)} AND tablename = {Lit(TableName)} AND indexname = {Lit(MarkerIndex)}");

    private static string ColumnType(IDbCommand cmd, string schema, string column)
        => Str(cmd, "SELECT data_type || COALESCE('(' || character_maximum_length || ')', '') "
                    + $"FROM information_schema.columns WHERE table_schema = {Lit(schema)} "
                    + $"AND table_name = {Lit(TableName)} AND column_name = {Lit(column)}");

    private static int RowCount(IDbCommand cmd, string schema) => Int(cmd, $"SELECT COUNT(*) FROM \"{schema}\".\"{TableName}\"");

    private static int AuditCount(IDbCommand cmd, string schema, string action)
        => Int(cmd, "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" "
                    + $"WHERE \"ObjectName\" = {Lit(schema + "." + TableName)} AND \"ActionType\" = {Lit(action)}");

    private int OwnershipRowCount(IDbCommand cmd, string schema)
        => Int(cmd, "SELECT COUNT(*) FROM \"SchemaSmith\".\"ProductOwnership\" "
                    + $"WHERE \"Schema\" = {Lit(schema)} AND \"TableName\" = {Lit(TableName)} "
                    + $"AND \"IndexName\" IS NULL AND \"ProductName\" = {Lit(_productName)}");

    /// <summary>The live column order as one comma-joined string, so an assertion reads as the shape a
    /// package author would recognise rather than as a set of index comparisons.</summary>
    private static string DeployedOrder(IDbCommand cmd, string schema)
        => Str(cmd, "SELECT string_agg(column_name::TEXT, ',' ORDER BY ordinal_position) "
                    + $"FROM information_schema.columns WHERE table_schema = {Lit(schema)} AND table_name = {Lit(TableName)}");

    /// <summary>The highest ordinal_position in use. PostgreSQL reports attnum here and never renumbers it,
    /// so this exceeding the column COUNT is direct evidence that a drop left a gap.</summary>
    private static int MaxOrdinal(IDbCommand cmd, string schema)
        => Int(cmd, "SELECT MAX(ordinal_position::INT) FROM information_schema.columns "
                    + $"WHERE table_schema = {Lit(schema)} AND table_name = {Lit(TableName)}");

    // ---- package shapes -----------------------------------------------------

    /// <summary>
    /// Three VARCHAR columns whose declared width is the only thing that varies, so the number of
    /// column-modification passes a deploy produces is exactly the number of widths that differ from the
    /// deployed 10. Marker never varies -- it must stay out of the change count so the index on it is never
    /// dropped as a dependent of a changing column.
    /// </summary>
    /// <param name="swapBC">Declares C before B. Nothing about either column changes -- only their order --
    /// so a deploy of this shape produces ZERO column modifications and the ONLY thing that can elect it is
    /// the order comparison.</param>
    /// <param name="includeD">Declares a fourth VARCHAR between A and B. Dropping it on a later deploy
    /// leaves the attnum gap in the middle of the table that the gap test needs.</param>
    private static string Package(string schema, int aWidth = 10, int bWidth = 10, int cWidth = 10,
        string rebuildPolicy = null, bool swapBC = false, bool includeD = false)
    {
        var policy = rebuildPolicy == null ? "" : $"\"RebuildPolicy\": {rebuildPolicy},";
        var columns = new List<string>
        {
            """{"Name": "Id", "DataType": "INT", "Nullable": false}""",
            """{"Name": "Marker", "DataType": "INT", "Nullable": true}""",
            $$"""{"Name": "A", "DataType": "VARCHAR({{aWidth}})", "Nullable": true}"""
        };
        if (includeD) columns.Add("""{"Name": "D", "DataType": "VARCHAR(10)", "Nullable": true}""");
        var b = $$"""{"Name": "B", "DataType": "VARCHAR({{bWidth}})", "Nullable": true}""";
        var c = $$"""{"Name": "C", "DataType": "VARCHAR({{cWidth}})", "Nullable": true}""";
        columns.Add(swapBC ? c : b);
        columns.Add(swapBC ? b : c);

        return $$"""
            {
                "Schema": "{{schema}}",
                "Name": "{{TableName}}",
                {{policy}}
                "Columns": [
                    {{string.Join(",\n        ", columns)}}
                ]
            }
            """;
    }

    /// <summary>
    /// Deploys the table at its baseline shape through the real quench (so it carries the ownership row a
    /// live table would have), gives it rows and the live-only marker index, and asserts that starting
    /// state. Every test begins here so a later "nothing changed" assertion is measured against a state
    /// that was verified, not assumed.
    /// </summary>
    private string Arrange(IDbCommand cmd, string prefix, bool includeD = false)
    {
        var schema = NewSchema(prefix);
        Exec(cmd, $"CREATE SCHEMA \"{schema}\";");
        Quench(cmd, Package(schema, includeD: includeD));
        Exec(cmd, includeD
            ? $"INSERT INTO \"{schema}\".\"{TableName}\" (\"Id\", \"Marker\", \"A\", \"D\", \"B\", \"C\") "
              + "VALUES (1, 10, 'a1', 'd1', 'b1', 'c1'), (2, 20, 'a2', 'd2', 'b2', 'c2');"
            : $"INSERT INTO \"{schema}\".\"{TableName}\" (\"Id\", \"Marker\", \"A\", \"B\", \"C\") "
              + "VALUES (1, 10, 'a1', 'b1', 'c1'), (2, 20, 'a2', 'b2', 'c2');");
        Exec(cmd, $"CREATE INDEX \"{MarkerIndex}\" ON \"{schema}\".\"{TableName}\" (\"Marker\");");

        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "Setup precondition: the live-only marker index must exist, or its absence later proves nothing.");
        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(10)"),
            "Setup precondition: column A must start at the narrow width the later package widens.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Setup precondition: the table must hold rows to lose.");
        Assert.That(OwnershipRowCount(cmd, schema), Is.EqualTo(1),
            "Setup precondition: the baseline quench must have claimed ownership of the table.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "Setup precondition: the baseline deploy must not itself have rebuilt anything.");
        return schema;
    }

    private static void Cleanup(IDbCommand cmd, string schema) => Exec(cmd, $"DROP SCHEMA \"{schema}\" CASCADE;");

    // ---- THE SAFETY PROPERTY ------------------------------------------------

    [Test]
    public void NoPolicyAnywhere_AltersInPlace_AndNeverRebuilds()
    {
        // THE test this whole slice exists to satisfy. A rebuild moves user data, so an ordinary table --
        // one with real column changes and no RebuildPolicy at any level -- must behave exactly as it did
        // before the decision point existed. Not "does not happen to be rebuilt": cannot be.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdNone");
        var oidBefore = Oid(cmd, schema, TableName);

        // Three column modifications -- comfortably more than any threshold a default would plausibly use,
        // so this is not passing merely because there was too little change to trip anything.
        Quench(cmd, Package(schema, aWidth: 50, bWidth: 50, cWidth: 50));

        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(50)"),
            "The in-place alter must actually have happened. Without this the no-rebuild assertions below "
            + "would pass just as happily on a quench that did nothing at all.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "The live-only marker index must survive. A rebuild drops the old table whole and re-creates no "
            + "index the package does not declare, so a missing marker is a replaced table.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore),
            "The table must be the SAME relation. A rebuild swaps in a new one.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "No 'rebuilt' audit row may exist for a table that never asked to be rebuilt.");
        Assert.That(AuditCount(cmd, schema, "wouldRebuild"), Is.Zero,
            "Nor a 'wouldRebuild' one -- the decision must not even have considered this table elected.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows are untouched by an in-place alter.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void ModeNever_Explicitly_AltersInPlace_AndNeverRebuilds()
    {
        // The same outcome reached by SAYING NEVER rather than by saying nothing. Both have to work: the
        // default is what protects packages that predate the feature, the explicit value is what lets a
        // table opt OUT of a product- or environment-level policy that would otherwise catch it.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdNever");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, aWidth: 50, bWidth: 50, cWidth: 50, rebuildPolicy: """{"Mode": "NEVER"}"""));

        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(50)"), "The in-place alter must have happened.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1), "NEVER must leave the live-only marker index standing.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "NEVER must leave the same relation in place.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero, "NEVER must record no rebuild.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void TableLevelNever_OverridesAnAlwaysProductPolicy()
    {
        // The opt-out that matters operationally: one table inside a product that rebuilds everything.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOptOut");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, aWidth: 50, rebuildPolicy: """{"Mode": "NEVER"}"""), mode: "ALWAYS");

        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(50)"), "The in-place alter must have happened.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "The table's own NEVER outranks the product's ALWAYS -- the marker index must survive.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "Same relation: no rebuild.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero, "No rebuild may be recorded.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    // ---- ALWAYS -------------------------------------------------------------

    [Test]
    public void ModeAlways_RebuildsOnAnyChange_AndKeepsRowsAndOwnership()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdAlways");
        var oidBefore = Oid(cmd, schema, TableName);

        // ONE modification. ALWAYS means any change is enough, so a single widened column has to be.
        Quench(cmd, Package(schema, aWidth: 50, rebuildPolicy: """{"Mode": "ALWAYS"}"""));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "ALWAYS with a detected change must elect a rebuild and the run manifest must say so.");
        Assert.That(Oid(cmd, schema, TableName), Is.Not.EqualTo(oidBefore),
            "The table must actually have been replaced -- a new OID is the only proof.");
        Assert.That(MarkerCount(cmd, schema), Is.Zero,
            "The live-only marker index went with the old table, which is what a rebuild does with anything "
            + "the package does not declare.");
        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(50)"),
            "The rebuilt table must be built to the DECLARED definition, not the old one.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2),
            "Carrying the rows across is the whole point; a rebuild that loses them is data destruction with "
            + "a successful exit code.");
        Assert.That(Str(cmd, $"SELECT \"A\" FROM \"{schema}\".\"{TableName}\" WHERE \"Id\" = 2"), Is.EqualTo("a2"),
            "Values must arrive intact and paired with the same keys -- a copy that shifted columns would "
            + "still produce two rows.");
        Assert.That(OwnershipRowCount(cmd, schema), Is.EqualTo(1),
            "The rebuilt table must still be owned by this product after the run, or the next deploy's "
            + "ownership prune treats it as somebody else's.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void ModeAlways_WithNoChangeAtAll_DoesNotRebuild()
    {
        // ALWAYS is "always instead of altering in place", not "always". An idempotent re-deploy of an
        // unchanged package must stay a no-op, or every deploy of a stable schema moves every row.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdAlwaysNoop");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, rebuildPolicy: """{"Mode": "ALWAYS"}"""));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "No change was detected, so there was nothing for a rebuild to deliver.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "Same relation: no rebuild.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1), "The marker index must survive an unchanged deploy.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    // ---- THRESHOLD ----------------------------------------------------------

    [Test]
    public void ModeThreshold_BelowTheBoundary_AltersInPlace()
    {
        // The low side of the boundary. Without this test a threshold implementation that fires on ANY
        // change (or never fires) still passes the high-side test.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdBelow");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, aWidth: 50, bWidth: 50, rebuildPolicy: """{"Mode": "THRESHOLD", "Threshold": 3}"""));

        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(50)"),
            "The in-place alters must have happened -- otherwise this asserts nothing about the threshold.");
        Assert.That(ColumnType(cmd, schema, "B"), Is.EqualTo("character varying(50)"), "Both modifications must have landed.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero, "Two modifications is below a threshold of three.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "Same relation: no rebuild.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1), "The marker index must survive an in-place alter.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void ModeThreshold_AtTheBoundary_Rebuilds()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdAt");
        var oidBefore = Oid(cmd, schema, TableName);

        // Three modified columns against a threshold of three: the comparison is >=, so the boundary itself
        // fires. Paired with the below-boundary test, this pins WHERE the boundary is, not just that one exists.
        Quench(cmd, Package(schema, aWidth: 50, bWidth: 50, cWidth: 50,
            rebuildPolicy: """{"Mode": "THRESHOLD", "Threshold": 3}"""));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1), "Three modifications reaches a threshold of three.");
        Assert.That(Oid(cmd, schema, TableName), Is.Not.EqualTo(oidBefore), "The table must actually have been replaced.");
        Assert.That(MarkerCount(cmd, schema), Is.Zero, "A rebuilt table does not carry the live-only marker index.");
        Assert.That(ColumnType(cmd, schema, "C"), Is.EqualTo("character varying(50)"), "The replacement is built to the declared definition.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows survive the rebuild.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    // ---- whole-object resolution -------------------------------------------

    [Test]
    public void TableLevelPolicy_WinsWhole_AndDoesNotInheritTheProductThreshold()
    {
        // The options-blender guard. The table declares ONLY a Mode; the product declares THRESHOLD 5. If
        // resolution were a per-field COALESCE the table would come out as ALWAYS *with Threshold 5*, and a
        // single-modification deploy would not rebuild. The table asked for ALWAYS and gets ALWAYS, whole.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdWhole");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, aWidth: 50, rebuildPolicy: """{"Mode": "ALWAYS"}"""),
            mode: "THRESHOLD", threshold: 5);

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "The table's whole policy is ALWAYS. Grafting the product's Threshold of 5 onto it would leave "
            + "one modification below the bar and skip the rebuild the package asked for.");
        Assert.That(Oid(cmd, schema, TableName), Is.Not.EqualTo(oidBefore), "The table must actually have been replaced.");
        Assert.That(MarkerCount(cmd, schema), Is.Zero, "A rebuilt table does not carry the live-only marker index.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows survive the rebuild.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void ProductLevelPolicy_AppliesToATableThatDeclaredNone()
    {
        // The other half of the cascade: a table with no policy of its own inherits the resolved upper tier
        // WHOLE. Without this the "table wins" test above would be satisfied by a build that simply ignored
        // the product tier entirely.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdProduct");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, aWidth: 50), mode: "ALWAYS");

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "The table declared no policy, so the product-level ALWAYS applies to it.");
        Assert.That(Oid(cmd, schema, TableName), Is.Not.EqualTo(oidBefore), "The table must actually have been replaced.");
        Assert.That(MarkerCount(cmd, schema), Is.Zero, "A rebuilt table does not carry the live-only marker index.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    // ---- WhatIf -------------------------------------------------------------

    [Test]
    public void WhatIf_WithARebuildElected_RecordsWouldRebuild_AndChangesNothing()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdWhatIf");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, aWidth: 50, rebuildPolicy: """{"Mode": "ALWAYS"}"""), whatIf: true);

        Assert.That(AuditCount(cmd, schema, "wouldRebuild"), Is.EqualTo(1),
            "A preview has to say the rebuild would happen, or an operator reads a preview that hides the one "
            + "operation in SchemaSmith that destroys data.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero, "WhatIf must record no actual rebuild.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "WhatIf must leave the same relation in place.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1), "WhatIf must not drop the live-only marker index.");
        Assert.That(ColumnType(cmd, schema, "A"), Is.EqualTo("character varying(10)"),
            "WhatIf must not widen the column either -- the elected rebuild must not have been quietly "
            + "replaced by an in-place alter.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "WhatIf touches no rows.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    // ---- OnOrderMismatch ----------------------------------------------------

    [Test]
    public void OnOrderMismatch_WithDriftedColumnOrder_Rebuilds_AndRestoresTheDeclaredOrder()
    {
        // Reordering existing columns is impossible in place on PostgreSQL, so a rebuild is the only thing
        // that can deliver it. The package swaps B and C and changes NOTHING else, so there is not a single
        // column modification to detect -- if this rebuilds, the order comparison is what elected it.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOrderDrift");
        var oidBefore = Oid(cmd, schema, TableName);
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,B,C"),
            "Setup precondition: the baseline must be deployed in the declared order, or the swap below is "
            + "not the thing under test.");

        Quench(cmd, Package(schema, rebuildPolicy: """{"OnOrderMismatch": true}""", swapBC: true));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "Drifted column order with OnOrderMismatch set must elect a rebuild and the run manifest must "
            + "say so.");
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,C,B"),
            "The rebuild has to actually FIX the order. A 'rebuilt' audit row over a table still in the old "
            + "order would mean the trigger fires forever without ever converging.");
        Assert.That(Oid(cmd, schema, TableName), Is.Not.EqualTo(oidBefore),
            "The relation must actually have been replaced, not merely audited.");
        Assert.That(MarkerCount(cmd, schema), Is.Zero,
            "The live-only marker index went with the old table, which is what a rebuild does with anything "
            + "the package does not declare.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2),
            "Carrying the rows across is the whole point; a rebuild that loses them is data destruction with "
            + "a successful exit code.");
        Assert.That(Str(cmd, $"SELECT \"B\" FROM \"{schema}\".\"{TableName}\" WHERE \"Id\" = 2"), Is.EqualTo("b2"),
            "Values must land in the right columns. A copy that reordered the SELECT but not the INSERT "
            + "would still produce two rows in the right order with the data transposed.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void OnOrderMismatch_AfterTheRebuild_ASecondIdenticalDeployDoesNotRebuildAgain()
    {
        // THE acceptance test for this trigger. A trigger that cannot converge is worse than no trigger:
        // every deploy would copy every row of the table forever. Deploy the drift, confirm the rebuild,
        // then deploy the IDENTICAL package again and require that nothing happens the second time.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOrderIdem");
        var package = Package(schema, rebuildPolicy: """{"OnOrderMismatch": true}""", swapBC: true);

        Quench(cmd, package);

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "Setup precondition: the first deploy must have rebuilt, or the second deploy proves nothing.");
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,C,B"),
            "Setup precondition: the order must actually have been fixed before convergence can be tested.");
        var oidAfterRebuild = Oid(cmd, schema, TableName);

        // The rebuild took the marker index with the old table, so re-create it: without a live-only object
        // in place there is nothing for the second deploy's "was this table replaced?" check to read.
        Exec(cmd, $"CREATE INDEX \"{MarkerIndex}\" ON \"{schema}\".\"{TableName}\" (\"Marker\");");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "Setup precondition: the fresh marker index must exist before the second deploy.");

        Quench(cmd, package);

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "STILL one. A second 'rebuilt' row means the order comparison re-elected a table it had just "
            + "fixed -- an infinite rebuild loop that moves every row of the table on every deploy.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidAfterRebuild),
            "The SAME relation must still be in place. This is the independent proof of convergence: even if "
            + "an audit row were somehow missed, a replaced table gets a new oid.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "The marker index must survive the second deploy.");
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,C,B"),
            "The order must still match the declaration -- the state the second deploy found and correctly "
            + "left alone.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows are untouched by a deploy that did nothing.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void OrderDrift_WithoutOnOrderMismatch_DoesNotRebuild()
    {
        // The trigger is opt-in like everything else in RebuildPolicy. The same drift that rebuilds above
        // must be ignored entirely when the package did not ask for it -- a rebuild moves user data, so it
        // must never be something a package gets without saying so.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOrderOptIn");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, swapBC: true));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "No policy asked for a rebuild on order drift, so drifted order must not produce one.");
        Assert.That(AuditCount(cmd, schema, "wouldRebuild"), Is.Zero,
            "Nor a 'wouldRebuild' one -- the decision must not even have considered this table elected.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "The same relation must still be in place.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "The live-only marker index must survive, which is what proves the table was not replaced.");
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,B,C"),
            "The deployed order must be left exactly as it was. This is also the anti-vacuity check: the "
            + "package really did declare a different order, so the assertions above are about a table that "
            + "genuinely had drift and was correctly left alone.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void OnOrderMismatch_WithTheOrderAlreadyCorrect_DoesNotRebuild()
    {
        // The flag is a trigger, not a switch. Set on a table whose order already matches, it must find
        // nothing to do -- otherwise turning it on rebuilds every table in the package on every deploy.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOrderMatch");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, rebuildPolicy: """{"OnOrderMismatch": true}"""));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "The declared order already matches the deployed order, so there is no drift to fix.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidBefore), "The same relation must still be in place.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1), "The marker index must survive an unchanged deploy.");
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,B,C"), "The order is untouched.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows are untouched.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void OnOrderMismatch_AfterAMiddleColumnIsDropped_DoesNotRebuild()
    {
        // THE INFINITE-LOOP GUARD. The comparison must be of RELATIVE sequence, never of absolute ordinal
        // positions. PostgreSQL reports attnum as ordinal_position and never renumbers it, so dropping a
        // column from the middle leaves the declared numbering (contiguous) and the live numbering
        // permanently offset. An equality comparison would report drift on a perfectly ordered table and
        // rebuild it on every single deploy, forever.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOrderGap", includeD: true);
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,D,B,C"),
            "Setup precondition: D must be deployed in the MIDDLE. Dropping a trailing column would leave no "
            + "offset behind it and the test would pass without exercising the trap.");

        // Deploy 1: D leaves the package and is dropped by absence. The remaining columns are still in
        // declared order relative to one another, so this must not rebuild either.
        Quench(cmd, Package(schema, rebuildPolicy: """{"OnOrderMismatch": true}"""));

        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,B,C"),
            "Setup precondition: D must actually have been dropped, or the deploy below is not the "
            + "post-drop state this test is about.");
        Assert.That(MaxOrdinal(cmd, schema), Is.EqualTo(6),
            "Setup precondition -- and the whole point of this test: five columns remain but the highest "
            + "ordinal_position is still 6, so the gap D left is real and every column after it now reports "
            + "a live position one higher than its declared one.");
        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "A column leaving the package is a metadata-only drop. The columns that remain are in declared "
            + "order, so there was no drift to elect on.");
        var oidAfterDrop = Oid(cmd, schema, TableName);

        // Deploy 2: the identical package against the post-drop table. This is the deploy that an
        // absolute-position comparison would rebuild.
        Quench(cmd, Package(schema, rebuildPolicy: """{"OnOrderMismatch": true}"""));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.Zero,
            "The table's columns are in exactly the declared order. Electing it here would mean the trigger "
            + "rebuilds every table that has ever lost a column, on every deploy, forever.");
        Assert.That(Oid(cmd, schema, TableName), Is.EqualTo(oidAfterDrop),
            "The same relation must still be in place after the second deploy.");
        Assert.That(MarkerCount(cmd, schema), Is.EqualTo(1),
            "The live-only marker index must have survived both deploys -- the proof the table was never "
            + "replaced.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows are untouched throughout.");

        Cleanup(cmd, schema);
        conn.Close();
    }

    [Test]
    public void OnOrderMismatch_ComposesWithModeNever_AndStillRebuildsOnDrift()
    {
        // OnOrderMismatch is an INDEPENDENT trigger, not a fourth Mode. Declared alongside an explicit NEVER
        // it must still fire: NEVER answers "rebuild instead of altering in place?", and order drift is not
        // an alter-in-place question at all -- there is no in-place answer to it.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var schema = Arrange(cmd, "RdOrderNever");
        var oidBefore = Oid(cmd, schema, TableName);

        Quench(cmd, Package(schema, rebuildPolicy: """{"Mode": "NEVER", "OnOrderMismatch": true}""", swapBC: true));

        Assert.That(AuditCount(cmd, schema, "rebuilt"), Is.EqualTo(1),
            "Mode NEVER must not suppress the order trigger. If it does, the headline use of this feature -- "
            + "'rebuild only when the column order drifts' -- cannot be expressed at all.");
        Assert.That(DeployedOrder(cmd, schema), Is.EqualTo("Id,Marker,A,C,B"),
            "And the rebuild must have delivered the declared order.");
        Assert.That(Oid(cmd, schema, TableName), Is.Not.EqualTo(oidBefore), "The relation must actually have been replaced.");
        Assert.That(MarkerCount(cmd, schema), Is.Zero, "A rebuilt table does not carry the live-only marker index.");
        Assert.That(RowCount(cmd, schema), Is.EqualTo(2), "Rows survive the rebuild.");

        Cleanup(cmd, schema);
        conn.Close();
    }
}
