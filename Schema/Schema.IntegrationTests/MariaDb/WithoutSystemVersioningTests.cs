// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB per-column <c>WITHOUT SYSTEM VERSIONING</c> — issue #408.
/// <para><b>The defect was a round-trip loss, not a missing feature.</b> The table-level
/// <c>IsSystemVersioned</c> shipped; the per-column exclusion that goes with it did not. Extraction never
/// read it, so extracting a versioned table and redeploying silently re-enabled history on a column the
/// author had deliberately excluded — usually because it is large or high-churn. Nothing errors; the
/// difference only shows up in what the history table accumulates.</para>
/// <para><b>The trap this feature had to avoid, found by probing the engine rather than by reading it.</b>
/// The two DDL paths disagree, and only one of them is forgiving. <c>CREATE TABLE</c> <i>accepts</i> the
/// clause on a table that is NOT system-versioned and <i>silently discards</i> it — <c>EXTRA</c> comes
/// back empty. <c>ALTER TABLE</c> does not: it fails with 4124, "Table is not system-versioned". So a
/// drift predicate comparing the declaration against the catalog is permanently unequal on such a table,
/// and every re-deploy would try to MODIFY the column — and every one of them would <b>fail outright</b>,
/// not merely churn. The predicate is therefore gated on the table actually being versioned;
/// <see cref="ExclusionOnANonVersionedTable_DoesNotChurn"/> pins it, and removing that gate is exactly
/// how the 4124 was observed.</para>
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class WithoutSystemVersioningTests
{
    private const string TableName = "without_sv_test";
    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        SetVersionOverride(null);
        Exec("SET @ss_system_versioning_alter_history = NULL");
        DropTestTable();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
    }

    [TearDown]
    public void TearDown()
    {
        SetVersionOverride(null);
        DropTestTable();
    }

    // ---- helpers -----------------------------------------------------------

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    /// <summary>
    /// Altering a column on a system-versioned table needs @@system_versioning_alter_history = KEEP, and
    /// SchemaSmith deliberately makes that the user's opt-in (SystemVersioningAlterHistory) because KEEP
    /// rewrites the STORED history to a shape it never had. Setting it here is the caller doing what a
    /// real package author has to do, not a workaround.
    /// </summary>
    private void AllowVersionedAlter() => Exec("SET @ss_system_versioning_alter_history = 'KEEP'");

    private void SetVersionOverride(int? majorMinor) =>
        Exec(majorMinor.HasValue ? $"SET @schemasmith_version_override = {majorMinor.Value}" : "SET @schemasmith_version_override = NULL");

    private void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    private static string BuildTableJson(bool versionedTable, bool excludeColumn)
    {
        var table = new MariaDbTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            IsSystemVersioned = versionedTable,
            Columns =
            [
                new MariaDbColumn { Name = "`id`", DataType = "INT", Nullable = false },
                new MariaDbColumn { Name = "`payload`", DataType = "INT", Nullable = true, WithoutSystemVersioning = excludeColumn }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        return "[" + JsonConvert.SerializeObject(table) + "]";
    }

    private void Deploy(bool versionedTable, bool excludeColumn)
    {
        var json = BuildTableJson(versionedTable, excludeColumn).Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('WithoutSvProductMdb', '{_testDb}', '{json}', 0, 0, 0)");
    }

    // Converge an EXISTING ordinary table to versioned AND add a NEW excluded column, in one deploy —
    // the #1/#13 scenario: previously the ADD COLUMN carried WITHOUT SYSTEM VERSIONING against a
    // not-yet-versioned table and aborted with MariaDB ERROR 4124.
    private void DeployConvergeAddingExcludedColumn()
    {
        var table = new MariaDbTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            IsSystemVersioned = true,
            Columns =
            [
                new MariaDbColumn { Name = "`id`", DataType = "INT", Nullable = false },
                new MariaDbColumn { Name = "`payload`", DataType = "INT", Nullable = true },
                new MariaDbColumn { Name = "`notes`", DataType = "INT", Nullable = true, WithoutSystemVersioning = true }
            ],
            Indexes = [new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }]
        };
        var json = ("[" + JsonConvert.SerializeObject(table) + "]").Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('WithoutSvProductMdb', '{_testDb}', '{json}', 0, 0, 0)");
    }

    /// <summary>
    /// Creates the versioned table directly, because SchemaSmith cannot: IsSystemVersioned is
    /// extract-only today -- nothing in any deploy script emits WITH SYSTEM VERSIONING, so a package
    /// declaring it deploys a plain table. That is a separate gap, reported on its own. It also happens
    /// to be the only way a versioned table reaches SchemaSmith in the field: it is adopted, not created.
    /// </summary>
    private void CreateVersionedTableOutOfBand(bool excludeColumn)
    {
        // System versioning does not exist below MariaDB 10.3, so WITH SYSTEM VERSIONING is a hard syntax
        // error on the 10.2 floor -- the scenario these tests exercise cannot even be SET UP there. Skip,
        // the same way InvisibleColumnGatingTests skips below 10.3: this reads the REAL server, because
        // SetUp clears @schemasmith_version_override and every test that calls this helper does so before
        // setting any override. The product's own suppression on < 10.3 is proven separately by the
        // override-driven degrade tests, which never create a real versioned table.
        if (Scalar("SELECT SchemaSmith_SupportsSystemVersioning()") == 0)
            Assert.Ignore("Target does not support system versioning (MariaDB < 10.3); a versioned table cannot be created to exercise the column exclusion.");

        DropTestTable();
        Exec($@"CREATE TABLE `{_testDb}`.`{TableName}` (
                  `id` INT NOT NULL,
                  `payload` INT NULL{(excludeColumn ? " WITHOUT SYSTEM VERSIONING" : "")},
                  PRIMARY KEY (`id`)
                ) ENGINE=InnoDB WITH SYSTEM VERSIONING");
    }

    private bool ColumnIsExcluded() => ColumnIsExcluded("payload");

    private bool ColumnIsExcluded(string column) =>
        (ScalarStr($@"SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = '{column}'") ?? "")
        .Contains("WITHOUT SYSTEM VERSIONING", StringComparison.OrdinalIgnoreCase);

    private long DowngradedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'downgraded' AND ObjectName LIKE '%{TableName}.payload%'");

    private long ModifiedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'modified' AND ObjectName LIKE '%{TableName}.payload%'");

    private string ExtractedJson() => ScalarStr($"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')");

    // ---- the feature -------------------------------------------------------

    [Test]
    public void ADeclaredExclusion_IsApplied()
    {
        AllowVersionedAlter();
        CreateVersionedTableOutOfBand(excludeColumn: false);
        Assert.That(ColumnIsExcluded(), Is.False, "precondition: adopted table has no exclusion yet");

        Deploy(versionedTable: true, excludeColumn: true);

        Assert.That(ColumnIsExcluded(), Is.True,
            "the column has to actually carry the exclusion, not merely deploy without error");
    }

    [Test]
    public void ConvergingToVersioned_WhileAddingAnExcludedColumn_DoesNotAbort_AndAppliesTheExclusion()
    {
        // #1 + #13: an existing ORDINARY table converging to versioned AND gaining a NEW excluded column in
        // the SAME deploy. Previously the ADD COLUMN carried WITHOUT SYSTEM VERSIONING against a
        // not-yet-versioned table and aborted the whole deploy with MariaDB ERROR 4124; and even avoiding
        // that, STEP 3 could not apply the exclusion the same deploy (it is gated on the table already being
        // versioned, which does not happen until STEP 7.5).
        if (Scalar("SELECT SchemaSmith_SupportsSystemVersioning()") == 0)
            Assert.Ignore("MariaDB < 10.3: system versioning unavailable.");
        AllowVersionedAlter(); // opt-in so STEP 7.6's post-versioning column MODIFY is permitted (else 4119)

        Deploy(versionedTable: false, excludeColumn: false); // a plain, non-versioned table (id, payload)

        Assert.DoesNotThrow(() => DeployConvergeAddingExcludedColumn(),
            "converging to versioned while adding an excluded column must not abort (was MariaDB ERROR 4124)");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar($@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND TABLE_TYPE = 'SYSTEM VERSIONED'"),
                Is.EqualTo(1), "the table must actually be system-versioned after the converge");
            Assert.That(ColumnIsExcluded("notes"), Is.True,
                "the newly-added excluded column must carry WITHOUT SYSTEM VERSIONING once the table is versioned");
        });
    }

    [Test]
    public void AnExcludedColumn_WritesNoHistoryRow()
    {
        // The outcome test. Everything else here reads catalog state; this reads what a user notices,
        // which is what the history table accumulates. Without the exclusion this UPDATE produces a
        // second row.
        AllowVersionedAlter();
        CreateVersionedTableOutOfBand(excludeColumn: false);
        Deploy(versionedTable: true, excludeColumn: true);
        Exec($"INSERT INTO `{_testDb}`.`{TableName}` (id, payload) VALUES (1, 10)");
        Exec($"UPDATE `{_testDb}`.`{TableName}` SET payload = 20 WHERE id = 1");

        Assert.That(Scalar($"SELECT COUNT(*) FROM `{_testDb}`.`{TableName}` FOR SYSTEM_TIME ALL"), Is.EqualTo(1),
            "an UPDATE touching only an excluded column must not write history -- that exclusion is "
            + "usually there because the column is large or high-churn");
    }

    [Test]
    public void AVersionedColumn_StillWritesHistory()
    {
        // The negative half: an exclusion applied too broadly would pass the test above while silently
        // stopping history on columns that are supposed to have it.
        CreateVersionedTableOutOfBand(excludeColumn: false);
        Deploy(versionedTable: true, excludeColumn: false);
        Exec($"INSERT INTO `{_testDb}`.`{TableName}` (id, payload) VALUES (1, 10)");
        Exec($"UPDATE `{_testDb}`.`{TableName}` SET payload = 20 WHERE id = 1");

        Assert.That(Scalar($"SELECT COUNT(*) FROM `{_testDb}`.`{TableName}` FOR SYSTEM_TIME ALL"), Is.EqualTo(2),
            "a column that is NOT excluded must still be versioned");
    }

    [Test]
    public void TheExclusion_RoundTripsThroughExtraction()
    {
        AllowVersionedAlter();
        // The #408 defect itself.
        CreateVersionedTableOutOfBand(excludeColumn: false);
        Deploy(versionedTable: true, excludeColumn: true);

        var json = ExtractedJson() ?? "";

        Assert.That(json, Does.Contain("WithoutSystemVersioning"),
            "an extracted package that drops this redeploys a table whose excluded column silently "
            + "starts accumulating history again.\n" + json);
    }

    [Test]
    public void ATableWithNoExclusion_ExtractsWithoutTheProperty()
    {
        // Emitting it for every column would rewrite every MySQL and MariaDB package for a setting
        // nobody declared -- and on MySQL the property does not exist at all.
        CreateVersionedTableOutOfBand(excludeColumn: false);
        Deploy(versionedTable: true, excludeColumn: false);

        Assert.That(ExtractedJson() ?? "", Does.Not.Contain("WithoutSystemVersioning"), ExtractedJson());
    }

    [Test]
    public void TheExclusionIsIdempotent()
    {
        CreateVersionedTableOutOfBand(excludeColumn: true);
        Deploy(versionedTable: true, excludeColumn: true);
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
        Deploy(versionedTable: true, excludeColumn: true);

        Assert.Multiple(() =>
        {
            Assert.That(ColumnIsExcluded(), Is.True);
            Assert.That(ModifiedAuditCount(), Is.Zero,
                "a second identical deploy must not re-modify the column");
        });
    }

    [Test]
    public void ExclusionOnANonVersionedTable_DoesNotChurn()
    {
        // THE guard. CREATE accepts the clause here and silently discards it, so the catalog can never
        // report it back; the declaration and the catalog are therefore permanently unequal. Ungated,
        // every re-deploy tries to MODIFY this column -- and ALTER, unlike CREATE, REFUSES the clause on
        // a non-versioned table (4124), so the deploy does not merely churn, it breaks. Verified by
        // removing the gate: this test fails with 4124 rather than with an unexpected audit row.
        Deploy(versionedTable: false, excludeColumn: true);
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");

        Deploy(versionedTable: false, excludeColumn: true);

        Assert.That(ModifiedAuditCount(), Is.Zero,
            "the engine discards the clause here, so it can never be read back -- comparing against it "
            + "unconditionally means the column is modified on every deploy, forever");
    }

    [Test]
    public void ChangingTheExclusion_IsDetectedAndApplied()
    {
        AllowVersionedAlter();
        CreateVersionedTableOutOfBand(excludeColumn: false);
        Deploy(versionedTable: true, excludeColumn: false);
        Assert.That(ColumnIsExcluded(), Is.False, "precondition");

        Deploy(versionedTable: true, excludeColumn: true);

        Assert.That(ColumnIsExcluded(), Is.True,
            "drift on a versioned table IS real and must converge -- the churn guard above must not have "
            + "been bought by ignoring the property altogether");
    }

    // ---- degrade path (MariaDB < 10.3 simulated via version override) ------

    [Test]
    public void BelowFloor_TheClauseIsSuppressedAndTheColumnStillDeploys()
    {
        // Below MariaDB 10.3 the keyword is a hard syntax error, so it has to be suppressed at build
        // time rather than failing the whole statement. But suppressing it SILENTLY is the failure the
        // capability guard exists to prevent: the column comes out looking correct while keeping history
        // the package said not to keep. The 'downgraded' row is what keeps that discoverable, and it is
        // what the CapabilityRegistry row for this feature points at.
        SetVersionOverride(1002);

        Deploy(versionedTable: false, excludeColumn: true);

        Assert.Multiple(() =>
        {
            Assert.That(Scalar($@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'payload'"),
                Is.EqualTo(1), "the column must still be created below the floor -- Reduced, not Skipped");
            Assert.That(ColumnIsExcluded(), Is.False);
            Assert.That(DowngradedAuditCount(), Is.EqualTo(1),
                "a degrade nobody can see afterwards is the thing the manifest row exists to prevent");
        });
    }
}
