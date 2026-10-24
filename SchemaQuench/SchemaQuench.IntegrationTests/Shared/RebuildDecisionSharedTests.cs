// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// The rebuild DECISION point on the MySQL family: which tables ModifiedTableQuench elects for a rebuild,
/// and -- far more important -- which it must not. RebuildTableSharedTests covers the rebuild engine
/// itself; this fixture covers the one thing that decides whether that engine is ever pointed at a user's
/// table.
///
/// Everything here runs the real parse -> missing -> modified sequence rather than calling RebuildTable
/// directly, because the decision only exists inside ModifiedTableQuench and a test that reached around it
/// would prove nothing about it. The upper-tier policy is set the way DatabaseQuench sets it -- session
/// variables, not parameters (MySQL has no default parameter values, so a parameter would break every
/// direct caller; @ss_capture_would_drop is the established precedent).
///
/// ANTI-VACUITY. "Nothing happened" is the passing state for half of these tests, so every one of them has
/// to be able to tell "nothing happened" apart from "the test asserted nothing". MySQL has no object id to
/// compare across a rebuild, so the device is a LIVE-ONLY MARKER INDEX the package never declares:
/// RebuildTable drops the old table whole and re-creates nothing but columns, and no downstream pass
/// re-adds an index the package does not carry, so the marker surviving is proof the table was NOT replaced
/// and the marker being gone is proof it was. Every test also asserts its preconditions, so a setup that
/// silently failed to produce the drift under test cannot read as a pass.
/// </summary>
[Category("Integration")]
public abstract class RebuildDecisionSharedTests : BaseTableQuenchTests
{
    private const string MarkerIndex = "ix_rebuild_decision_marker";

    // ---- harness ------------------------------------------------------------

    private IDbConnection OpenMainDb()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var use = conn.CreateCommand();
        use.CommandText = $"USE `{_mainDb}`";
        use.ExecuteNonQuery();
        return conn;
    }

    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    /// <summary>
    /// Runs the real parse + missing + modified sequence with the resolved upper-tier policy in the same
    /// session variables DatabaseQuench uses. Passing no mode sets them to the no-policy values rather than
    /// leaving them unset, which is what DatabaseQuench does too -- a stale ALWAYS carried on a pooled
    /// connection would rebuild tables that never asked for it.
    /// </summary>
    private void Quench(IDbCommand cmd, string json, string? mode = null, int? threshold = null, bool whatIf = false)
    {
        var whatIfVal = whatIf ? 1 : 0;
        Exec(cmd, $"CALL SchemaSmith_ParseTableJson({Lit(_mainDb)}, {Lit(json)})");
        Exec(cmd, $"CALL SchemaSmith_MissingTableAndColumnQuench({Lit(_mainDb)}, {whatIfVal})");
        Exec(cmd, $"SET @ss_rebuild_policy_mode = {Lit(mode ?? "NEVER")}, "
                  + $"@ss_rebuild_policy_threshold = {(threshold.HasValue ? threshold.Value.ToString() : "NULL")}, "
                  + "@ss_rebuild_policy_on_order_mismatch = 0");
        // Trailing 0, 0 are p_DropUnknownIndexes / p_DropIndexesRemovedFromProduct: the marker index is
        // deliberately unknown to the package, and dropping it by absence would destroy the very signal
        // these tests read.
        Exec(cmd, $"CALL SchemaSmith_ModifiedTableQuench({Lit(_productName)}, {Lit(_mainDb)}, {whatIfVal}, 0, 1, 1, 1, 1, 0, 0, 0)");
    }

    private static object? Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static long Long(IDbCommand cmd, string sql)
    {
        var v = Scalar(cmd, sql);
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt64(v);
    }

    private static int Int(IDbCommand cmd, string sql) => (int)Long(cmd, sql);

    private static string? Str(IDbCommand cmd, string sql)
    {
        var v = Scalar(cmd, sql);
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    private int MarkerCount(IDbCommand cmd, string table)
        => Int(cmd, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                    + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)} AND INDEX_NAME = {Lit(MarkerIndex)}");

    private string? ColumnType(IDbCommand cmd, string table, string column)
        => Str(cmd, "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
                    + $"WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)} AND COLUMN_NAME = {Lit(column)}");

    private static int RowCount(IDbCommand cmd, string table) => Int(cmd, $"SELECT COUNT(*) FROM `{table}`");

    private static int AuditCount(IDbCommand cmd, string table, string action)
        => Int(cmd, "SELECT COUNT(*) FROM SchemaSmith_ChangeAudit "
                    + $"WHERE ObjectName = {Lit(table)} AND ActionType = {Lit(action)}");

    private int OwnershipRowCount(IDbCommand cmd, string table)
        => Int(cmd, "SELECT COUNT(*) FROM SchemaSmith_ProductOwnership "
                    + $"WHERE ObjectSchema = {Lit(_mainDb)} AND ObjectName = {Lit(table)} "
                    + $"AND ObjectType = 'TABLE' AND ProductName = {Lit(_productName)}");

    /// <summary>The live column order as one comma-joined string, so an assertion reads as the shape a
    /// package author would recognise rather than as a set of index comparisons.</summary>
    private string? DeployedOrder(IDbCommand cmd, string table)
        => Str(cmd, "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION SEPARATOR ',') "
                    + $"FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = {Lit(_mainDb)} AND TABLE_NAME = {Lit(table)}");

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
    /// leaves the gap in the middle of the engine's ordinal numbering that the gap test needs.</param>
    private static string Package(string table, int aWidth = 10, int bWidth = 10, int cWidth = 10,
        string? rebuildPolicy = null, bool swapBC = false, bool includeD = false)
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
            [{
                "Name": "{{table}}",
                {{policy}}
                "Columns": [
                    {{string.Join(",\n        ", columns)}}
                ]
            }]
            """;
    }

    /// <summary>
    /// Deploys the table at its baseline shape through the real quench sequence, gives it rows and the
    /// live-only marker index, and asserts that starting state. Every test begins here so a later "nothing
    /// changed" assertion is measured against a state that was verified, not assumed.
    /// </summary>
    private string Arrange(IDbCommand cmd, bool includeD = false)
    {
        var table = "RbDecision_" + Uid();
        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        Quench(cmd, Package(table, includeD: includeD));
        Exec(cmd, includeD
            ? $"INSERT INTO `{table}` (Id, Marker, A, D, B, C) VALUES (1, 10, 'a1', 'd1', 'b1', 'c1'), (2, 20, 'a2', 'd2', 'b2', 'c2')"
            : $"INSERT INTO `{table}` (Id, Marker, A, B, C) VALUES (1, 10, 'a1', 'b1', 'c1'), (2, 20, 'a2', 'b2', 'c2')");
        Exec(cmd, $"CREATE INDEX `{MarkerIndex}` ON `{table}` (Marker)");

        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "Setup precondition: the live-only marker index must exist, or its absence later proves nothing.");
        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(10)"),
            "Setup precondition: column A must start at the narrow width the later package widens.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Setup precondition: the table must hold rows to lose.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "Setup precondition: the baseline deploy must not itself have rebuilt anything.");
        return table;
    }

    // ---- THE SAFETY PROPERTY ------------------------------------------------

    [Test]
    public void NoPolicyAnywhere_AltersInPlace_AndNeverRebuilds()
    {
        // THE test this whole slice exists to satisfy. A rebuild moves user data, so an ordinary table --
        // one with real column changes and no RebuildPolicy at any level -- must behave exactly as it did
        // before the decision point existed. Not "does not happen to be rebuilt": cannot be.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);

        // Three column modifications -- comfortably more than any threshold a default would plausibly use,
        // so this is not passing merely because there was too little change to trip anything.
        Quench(cmd, Package(table, aWidth: 50, bWidth: 50, cWidth: 50));

        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(50)"),
            "The in-place alter must actually have happened. Without this the no-rebuild assertions below "
            + "would pass just as happily on a quench that did nothing at all.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "The live-only marker index must survive. A rebuild drops the old table whole and re-creates no "
            + "index the package does not declare, so a missing marker is a replaced table.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "No 'rebuilt' audit row may exist for a table that never asked to be rebuilt.");
        Assert.That(AuditCount(cmd, table, "wouldRebuild"), Is.Zero,
            "Nor a 'wouldRebuild' one -- the decision must not even have considered this table elected.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows are untouched by an in-place alter.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);

        Quench(cmd, Package(table, aWidth: 50, bWidth: 50, cWidth: 50, rebuildPolicy: """{"Mode": "NEVER"}"""));

        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(50)"), "The in-place alter must have happened.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1), "NEVER must leave the live-only marker index standing.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero, "NEVER must record no rebuild.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    [Test]
    public void TableLevelNever_OverridesAnAlwaysProductPolicy()
    {
        // The opt-out that matters operationally: one table inside a product that rebuilds everything.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);

        Quench(cmd, Package(table, aWidth: 50, rebuildPolicy: """{"Mode": "NEVER"}"""), mode: "ALWAYS");

        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(50)"), "The in-place alter must have happened.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "The table's own NEVER outranks the product's ALWAYS -- the marker index must survive.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero, "No rebuild may be recorded.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    // ---- ALWAYS -------------------------------------------------------------

    [Test]
    public void ModeAlways_RebuildsOnAnyChange_AndKeepsRowsAndOwnership()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);
        var ownedBefore = OwnershipRowCount(cmd, table);
        Assert.That(ownedBefore, Is.EqualTo(1),
            "Setup precondition: the baseline quench must have claimed ownership of the table.");

        // ONE modification. ALWAYS means any change is enough, so a single widened column has to be.
        Quench(cmd, Package(table, aWidth: 50, rebuildPolicy: """{"Mode": "ALWAYS"}"""));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "ALWAYS with a detected change must elect a rebuild and the run manifest must say so.");
        Assert.That(MarkerCount(cmd, table), Is.Zero,
            "The live-only marker index went with the old table, which is what a rebuild does with anything "
            + "the package does not declare -- and it is the only proof on this engine that the table was "
            + "actually replaced.");
        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(50)"),
            "The rebuilt table must be built to the DECLARED definition, not the old one.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2),
            "Carrying the rows across is the whole point; a rebuild that loses them is data destruction with "
            + "a successful exit code.");
        Assert.That(Str(cmd, $"SELECT A FROM `{table}` WHERE Id = 2"), Is.EqualTo("a2"),
            "Values must arrive intact and paired with the same keys -- a copy that shifted columns would "
            + "still produce two rows.");
        Assert.That(OwnershipRowCount(cmd, table), Is.EqualTo(1),
            "The rebuilt table must still be owned by this product after the run, or the next deploy's "
            + "ownership prune treats it as somebody else's.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    [Test]
    public void ModeAlways_WithNoChangeAtAll_DoesNotRebuild()
    {
        // ALWAYS is "always instead of altering in place", not "always". An idempotent re-deploy of an
        // unchanged package must stay a no-op, or every deploy of a stable schema moves every row.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);

        Quench(cmd, Package(table, rebuildPolicy: """{"Mode": "ALWAYS"}"""));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "No change was detected, so there was nothing for a rebuild to deliver.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1), "The marker index must survive an unchanged deploy.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);

        Quench(cmd, Package(table, aWidth: 50, bWidth: 50, rebuildPolicy: """{"Mode": "THRESHOLD", "Threshold": 3}"""));

        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(50)"),
            "The in-place alters must have happened -- otherwise this asserts nothing about the threshold.");
        Assert.That(ColumnType(cmd, table, "B"), Is.EqualTo("varchar(50)"), "Both modifications must have landed.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero, "Two modifications is below a threshold of three.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1), "The marker index must survive an in-place alter.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    [Test]
    public void ModeThreshold_AtTheBoundary_Rebuilds()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);

        // Three modified columns against a threshold of three: the comparison is >=, so the boundary itself
        // fires. Paired with the below-boundary test, this pins WHERE the boundary is, not just that one exists.
        Quench(cmd, Package(table, aWidth: 50, bWidth: 50, cWidth: 50,
            rebuildPolicy: """{"Mode": "THRESHOLD", "Threshold": 3}"""));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1), "Three modifications reaches a threshold of three.");
        Assert.That(MarkerCount(cmd, table), Is.Zero, "A rebuilt table does not carry the live-only marker index.");
        Assert.That(ColumnType(cmd, table, "C"), Is.EqualTo("varchar(50)"), "The replacement is built to the declared definition.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows survive the rebuild.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);

        Quench(cmd, Package(table, aWidth: 50, rebuildPolicy: """{"Mode": "ALWAYS"}"""),
            mode: "THRESHOLD", threshold: 5);

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "The table's whole policy is ALWAYS. Grafting the product's Threshold of 5 onto it would leave "
            + "one modification below the bar and skip the rebuild the package asked for.");
        Assert.That(MarkerCount(cmd, table), Is.Zero, "A rebuilt table does not carry the live-only marker index.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows survive the rebuild.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);

        Quench(cmd, Package(table, aWidth: 50), mode: "ALWAYS");

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "The table declared no policy, so the product-level ALWAYS applies to it.");
        Assert.That(MarkerCount(cmd, table), Is.Zero, "A rebuilt table does not carry the live-only marker index.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    // ---- WhatIf -------------------------------------------------------------

    [Test]
    public void WhatIf_WithARebuildElected_RecordsWouldRebuild_AndChangesNothing()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);

        Quench(cmd, Package(table, aWidth: 50, rebuildPolicy: """{"Mode": "ALWAYS"}"""), whatIf: true);

        Assert.That(AuditCount(cmd, table, "wouldRebuild"), Is.EqualTo(1),
            "A preview has to say the rebuild would happen, or an operator reads a preview that hides the one "
            + "operation in SchemaSmith that destroys data.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero, "WhatIf must record no actual rebuild.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1), "WhatIf must not drop the live-only marker index.");
        Assert.That(ColumnType(cmd, table, "A"), Is.EqualTo("varchar(10)"),
            "WhatIf must not widen the column either -- the elected rebuild must not have been quietly "
            + "replaced by an in-place alter.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "WhatIf touches no rows.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    // ---- OnOrderMismatch ----------------------------------------------------

    [Test]
    public void OnOrderMismatch_WithDriftedColumnOrder_Rebuilds_AndRestoresTheDeclaredOrder()
    {
        // Reordering existing columns is impossible in place on this engine, so a rebuild is the only thing
        // that can deliver it. The package swaps B and C and changes NOTHING else, so there is not a single
        // column modification to detect -- if this rebuilds, the order comparison is what elected it.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,B,C"),
            "Setup precondition: the baseline must be deployed in the declared order, or the swap below is "
            + "not the thing under test.");

        Quench(cmd, Package(table, rebuildPolicy: """{"OnOrderMismatch": true}""", swapBC: true));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "Drifted column order with OnOrderMismatch set must elect a rebuild and the run manifest must "
            + "say so.");
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,C,B"),
            "The rebuild has to actually FIX the order. A 'rebuilt' audit row over a table still in the old "
            + "order would mean the trigger fires forever without ever converging.");
        Assert.That(MarkerCount(cmd, table), Is.Zero,
            "The live-only marker index went with the old table -- the only proof on this engine that the "
            + "table was genuinely replaced rather than merely audited.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2),
            "Carrying the rows across is the whole point; a rebuild that loses them is data destruction with "
            + "a successful exit code.");
        Assert.That(Str(cmd, $"SELECT B FROM `{table}` WHERE Id = 2"), Is.EqualTo("b2"),
            "Values must land in the right columns. A copy that reordered the SELECT but not the INSERT "
            + "would still produce two rows in the right order with the data transposed.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);
        var package = Package(table, rebuildPolicy: """{"OnOrderMismatch": true}""", swapBC: true);

        Quench(cmd, package);

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "Setup precondition: the first deploy must have rebuilt, or the second deploy proves nothing.");
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,C,B"),
            "Setup precondition: the order must actually have been fixed before convergence can be tested.");

        // The rebuild took the marker index with the old table, so re-create it: without a live-only object
        // in place there is nothing for the second deploy's "was this table replaced?" check to read.
        Exec(cmd, $"CREATE INDEX `{MarkerIndex}` ON `{table}` (Marker)");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "Setup precondition: the fresh marker index must exist before the second deploy.");

        Quench(cmd, package);

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "STILL one. A second 'rebuilt' row means the order comparison re-elected a table it had just "
            + "fixed -- an infinite rebuild loop that moves every row of the table on every deploy.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "The marker index must survive the second deploy. Its absence would prove the table was replaced "
            + "again even if the audit row somehow were not written.");
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,C,B"),
            "The order must still match the declaration -- the state the second deploy found and correctly "
            + "left alone.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows are untouched by a deploy that did nothing.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);

        Quench(cmd, Package(table, swapBC: true));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "No policy asked for a rebuild on order drift, so drifted order must not produce one.");
        Assert.That(AuditCount(cmd, table, "wouldRebuild"), Is.Zero,
            "Nor a 'wouldRebuild' one -- the decision must not even have considered this table elected.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "The live-only marker index must survive, which is what proves the table was not replaced.");
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,B,C"),
            "The deployed order must be left exactly as it was. This is also the anti-vacuity check: the "
            + "package really did declare a different order, so the assertions above are about a table that "
            + "genuinely had drift and was correctly left alone.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    [Test]
    public void OnOrderMismatch_WithTheOrderAlreadyCorrect_DoesNotRebuild()
    {
        // The flag is a trigger, not a switch. Set on a table whose order already matches, it must find
        // nothing to do -- otherwise turning it on rebuilds every table in the package on every deploy.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd);

        Quench(cmd, Package(table, rebuildPolicy: """{"OnOrderMismatch": true}"""));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "The declared order already matches the deployed order, so there is no drift to fix.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1), "The marker index must survive an unchanged deploy.");
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,B,C"), "The order is untouched.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows are untouched.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }

    [Test]
    public void OnOrderMismatch_AfterAMiddleColumnIsDropped_DoesNotRebuild()
    {
        // THE INFINITE-LOOP GUARD. The comparison must be of RELATIVE sequence, never of absolute ordinal
        // positions. A column dropped from the middle of a table leaves the declared numbering (1..n,
        // contiguous) and the live numbering permanently offset from each other on the engines that keep the
        // gap -- so an equality comparison would report drift on a perfectly ordered table and rebuild it on
        // every single deploy, forever.
        //
        // On MySQL/MariaDB ORDINAL_POSITION is renumbered contiguously after a drop, so this engine does not
        // expose the gap itself; the test is kept here as the regression guard for the shared definition and
        // its SQL Server / PostgreSQL twins are where the gap is genuinely present and asserted.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        var table = Arrange(cmd, includeD: true);
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,D,B,C"),
            "Setup precondition: D must be deployed in the MIDDLE. Dropping a trailing column would leave no "
            + "offset behind it and the test would pass without exercising the trap.");

        // Deploy 1: D leaves the package and is dropped by absence. The remaining columns are still in
        // declared order relative to one another, so this must not rebuild either.
        Quench(cmd, Package(table, rebuildPolicy: """{"OnOrderMismatch": true}"""));

        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,B,C"),
            "Setup precondition: D must actually have been dropped, or the deploy below is not the "
            + "post-drop state this test is about.");
        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "A column leaving the package is a metadata-only drop. The columns that remain are in declared "
            + "order, so there was no drift to elect on.");

        // Deploy 2: the identical package against the post-drop table. This is the deploy that an
        // absolute-position comparison would rebuild.
        Quench(cmd, Package(table, rebuildPolicy: """{"OnOrderMismatch": true}"""));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.Zero,
            "The table's columns are in exactly the declared order. Electing it here would mean the trigger "
            + "rebuilds every table that has ever lost a column, on every deploy, forever.");
        Assert.That(MarkerCount(cmd, table), Is.EqualTo(1),
            "The live-only marker index must have survived both deploys -- the proof the table was never "
            + "replaced.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows are untouched throughout.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
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

        var table = Arrange(cmd);

        Quench(cmd, Package(table, rebuildPolicy: """{"Mode": "NEVER", "OnOrderMismatch": true}""", swapBC: true));

        Assert.That(AuditCount(cmd, table, "rebuilt"), Is.EqualTo(1),
            "Mode NEVER must not suppress the order trigger. If it does, the headline use of this feature -- "
            + "'rebuild only when the column order drifts' -- cannot be expressed at all.");
        Assert.That(DeployedOrder(cmd, table), Is.EqualTo("Id,Marker,A,C,B"),
            "And the rebuild must have delivered the declared order.");
        Assert.That(MarkerCount(cmd, table), Is.Zero, "A rebuilt table does not carry the live-only marker index.");
        Assert.That(RowCount(cmd, table), Is.EqualTo(2), "Rows survive the rebuild.");

        Exec(cmd, $"DROP TABLE IF EXISTS `{table}`");
        conn.Close();
    }
}
