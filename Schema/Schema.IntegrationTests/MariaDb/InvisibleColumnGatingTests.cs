// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// Invisible-column version-gating for MariaDB. Unlike column DEFAULT expressions (always supported at
/// the 10.2 floor -- see Schema.IntegrationTests.MariaDb.DefaultExpressionGatingTests), invisible columns
/// have a genuine MariaDB threshold: 10.3.0. Below that the INVISIBLE keyword is a hard syntax error, so
/// the emit must degrade through SchemaSmith_UnsupportedFeaturePolicy exactly as it does on MySQL below
/// 8.0.23 -- see Schema.IntegrationTests.MySQL.InvisibleColumnGatingTests.
///
/// SchemaSmith_SupportsInvisibleColumn()'s MariaDB branch calls SchemaSmith_ServerVersionNum() (unlike
/// the DEFAULT-expression gate, whose MariaDB branch short-circuits on VERSION() LIKE '%MariaDB%' alone),
/// so @schemasmith_version_override DOES drive it here: overriding to 1002 simulates MariaDB below 10.3
/// on the modern CI container.
///
/// A second, genuine divergence (not version-gated -- verified live against MariaDB 11.4.12 and MySQL
/// 8.0.45): MariaDB requires a DEFAULT on a NOT NULL invisible column (error 4108, "must have a default
/// value"); MySQL does not. The shared fixture's `secret` column always carries a DEFAULT so it stays
/// legal on both engines; NotNullInvisibleColumnWithoutDefault_IsRejectedByEngine below pins the
/// divergence itself on a column built without one.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class InvisibleColumnGatingTests
{
    private const string TableName = "invisible_col_gate_test";
    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        // ForgeKindler is already deployed into MainDb by the fixture.
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
        SetPolicy(null);
        DropTestTable();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
    }

    [TearDown]
    public void TearDown()
    {
        SetVersionOverride(null);
        SetPolicy(null);
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

    private void SetVersionOverride(int? majorMinor) =>
        Exec(majorMinor.HasValue ? $"SET @schemasmith_version_override = {majorMinor.Value}" : "SET @schemasmith_version_override = NULL");

    private void SetPolicy(string policy) =>
        Exec(policy == null ? "SET @schemasmith_unsupported_policy = NULL" : $"SET @schemasmith_unsupported_policy = '{policy}'");

    private void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    // The secret column carries a DEFAULT so the fixture is portable across both engines: MariaDB
    // rejects a NOT NULL INVISIBLE column with no DEFAULT (error 4108 -- see
    // NotNullInvisibleColumnWithoutDefault_IsRejectedByEngine below, where that divergence is pinned
    // deliberately), MySQL does not. The DEFAULT is incidental to what these tests are actually about
    // (Invisible surviving extraction / visibility drift), so it stays fixed here rather than becoming
    // part of what any individual test varies -- withDefault exists only for the divergence test itself.
    private static string BuildTableJson(bool invisible, bool withDefault = true)
    {
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`secret`", DataType = "INT", Nullable = false, Invisible = invisible, Default = withDefault ? "0" : null }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        return "[" + JsonConvert.SerializeObject(table) + "]";
    }

    private void Deploy(bool invisible, bool withDefault = true)
    {
        var json = BuildTableJson(invisible, withDefault).Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('InvisibleColGateProductMdb', '{_testDb}', '{json}', 0, 0, 0)");
    }

    private long TableExistsCount() => Scalar(
        $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'");

    private long ColumnExistsCount() => Scalar(
        $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'secret'");

    private bool ColumnIsInvisible() =>
        (ScalarStr($@"SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'secret'") ?? "")
        .Contains("INVISIBLE", StringComparison.OrdinalIgnoreCase);

    private long DowngradedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'downgraded' AND ObjectName LIKE '%{TableName}.secret%'");

    private long ModifiedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'modified' AND ObjectName LIKE '%{TableName}.secret%'");

    // ---- degrade path (MariaDB < 10.3 simulated via version override) ------

    [Test]
    public void BelowFloor_Warn_DeploysTableCreatesColumnVisibleAndRecordsDowngraded()
    {
        SetVersionOverride(1002);
        SetPolicy("warn");

        Deploy(invisible: true);

        Assert.Multiple(() =>
        {
            Assert.That(TableExistsCount(), Is.EqualTo(1), "Table must still deploy on MariaDB < 10.3.");
            Assert.That(ColumnExistsCount(), Is.EqualTo(1), "The column must still be created below MariaDB 10.3 -- only its invisibility degrades.");
            Assert.That(ColumnIsInvisible(), Is.False, "Invisible column degrades to visible below MariaDB 10.3.");
            Assert.That(DowngradedAuditCount(), Is.EqualTo(1), "The degraded invisibility must record a 'downgraded' manifest row.");
        });
    }

    [Test]
    public void BelowFloor_Warn_SecondDeployIsIdempotent()
    {
        SetVersionOverride(1002);
        SetPolicy("warn");

        Deploy(invisible: true);
        Assert.DoesNotThrow(() => Deploy(invisible: true), "A second deploy below the floor must not error.");

        Assert.Multiple(() =>
        {
            Assert.That(ColumnIsInvisible(), Is.False, "The column must remain visible after a second deploy.");
            Assert.That(ModifiedAuditCount(), Is.EqualTo(0),
                "A second deploy must not record a spurious 'modified' row for the ignored visibility difference below the floor (non-idempotency bug).");
        });
    }

    [Test]
    public void BelowFloor_Fail_AbortsNamingTheOffendingColumn()
    {
        SetVersionOverride(1002);
        SetPolicy("fail");

        Assert.That(() => Deploy(invisible: true), Throws.Exception.With.Message.Contains("10.3"),
            "Under policy 'fail' a declared invisible column below the floor must abort the deploy.");
    }

    // ---- supported path (modern binary, no override) -----------------------

    [Test]
    public void ModernServer_CreatesColumnInvisible_AndExtractionRoundTripsTrue()
    {
        if (Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()") == 0)
            Assert.Ignore("Target does not support invisible columns (MariaDB < 10.3); covered by the BelowFloor_* tests.");

        SetPolicy("warn");
        Deploy(invisible: true);

        Assert.That(ColumnIsInvisible(), Is.True, "The column must be created invisible on the modern binary.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')";
        var json = "";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                json += reader[0];
        }
        // Pattern-matched rather than dereferenced with '!': the null case is real if the JSON
        // is wrong, and this fails on it explicitly instead of as a NullReferenceException.
        if (PlatformDeserializer.DeserializeTable(json, Platform.MariaDb) is not MySqlTable table)
        {
            Assert.Fail("Extraction must deserialize to a table.");
            return;
        }
        var secret = (MySqlColumn)table.Columns.Find(c => c.Name.Contains("secret"));

        Assert.That(secret, Is.Not.Null);
        Assert.That(secret!.Invisible, Is.True, "Extraction must round-trip the declared Invisible=true.");
    }

    [Test]
    public void ModernServer_DriftVisibleToInvisibleIsDetectedAndApplied()
    {
        if (Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()") == 0)
            Assert.Ignore("Target does not support invisible columns (MariaDB < 10.3); covered by the BelowFloor_* tests.");

        SetPolicy("warn");
        Deploy(invisible: false);
        Assert.That(ColumnIsInvisible(), Is.False, "Sanity: column starts visible.");

        Deploy(invisible: true);

        Assert.Multiple(() =>
        {
            Assert.That(ColumnIsInvisible(), Is.True, "A visible -> invisible change must be detected and applied.");
            Assert.That(ModifiedAuditCount(), Is.GreaterThanOrEqualTo(1), "The visibility change must record a 'modified' manifest row.");
        });
    }

    [Test]
    public void ModernServer_DriftInvisibleToVisibleIsDetectedAndApplied()
    {
        // The reverse direction: a naive implementation that only checks "declared invisible but
        // currently visible" misses this side.
        if (Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()") == 0)
            Assert.Ignore("Target does not support invisible columns (MariaDB < 10.3); covered by the BelowFloor_* tests.");

        SetPolicy("warn");
        Deploy(invisible: true);
        Assert.That(ColumnIsInvisible(), Is.True, "Sanity: column starts invisible.");

        Deploy(invisible: false);

        Assert.Multiple(() =>
        {
            Assert.That(ColumnIsInvisible(), Is.False, "An invisible -> visible change must be detected and applied.");
            Assert.That(ModifiedAuditCount(), Is.GreaterThanOrEqualTo(1), "The visibility change must record a 'modified' manifest row.");
        });
    }

    // ---- genuine engine divergence (not a SchemaSmith defect) ---------------

    [Test]
    public void NotNullInvisibleColumnWithoutDefault_IsRejectedByEngine()
    {
        // Pins a real MariaDB engine rule rather than a SchemaSmith defect: MariaDB requires a NOT NULL
        // invisible column to carry a DEFAULT (error 4108, "must have a default value"); MySQL has no
        // such requirement (verified live against MariaDB 11.4.12 and MySQL 8.0.45 -- see
        // docs/end-user/reference/schemaquench.md's MySQL/MariaDB divergence table). SchemaSmith does not
        // pre-validate this -- the codebase convention is to fail loudly through the engine's own error
        // rather than pre-check -- so this locks in that the 4108 error passes straight through
        // undisturbed instead of being swallowed or reworded.
        if (Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()") == 0)
            Assert.Ignore("Target does not support invisible columns (MariaDB < 10.3); the default-value rule cannot be exercised.");

        SetPolicy("warn");

        Assert.That(() => Deploy(invisible: true, withDefault: false),
            Throws.Exception.With.Message.Contains("must have a default value"),
            "MariaDB rejects a NOT NULL invisible column with no DEFAULT; SchemaSmith must pass the engine's own error through unmodified rather than pre-validating it.");
    }

    // ---- interaction with the pre-RENAME-COLUMN CHANGE COLUMN fallback ------
    //
    // Two tests, deliberately: MariaDB 10.3-10.5 is a real band where invisible columns are live
    // (SchemaSmith_SupportsInvisibleColumn needs only 1003) but RENAME COLUMN is not yet available
    // (SchemaSmith_SupportsRenameColumn needs 1006), so a rename in that band falls back to
    // CHANGE COLUMN <reconstructed-def> (SchemaSmith_MissingTableAndColumnQuench.sql:331-356+), whose
    // reconstruction omits both DEFAULT and INVISIBLE from the rebuilt definition.
    // RenameOfInvisibleColumnWithDefault_... uses the realistic NOT NULL + DEFAULT shape every other
    // test in this file uses; mutation testing showed the DEFAULT gap alone is enough to pull the
    // column into ModifiedTableQuench's MODIFY COLUMN and restore INVISIBLE as a side effect -- it does
    // NOT prove the visibility predicate does anything. RenameOfInvisibleColumnNullableNoDefault_...
    // removes DEFAULT from the picture entirely (nullable, no default declared on either side) so the
    // visibility predicate is the only thing that can restore INVISIBLE; confirmed by mutation --
    // killing the predicate (OR (1 = 0) at all five STEP 3 sites) reddens this one but not the other.

    // Shared post-rename verification for both tests below. The rename+data assertions are what proved
    // (via an earlier iteration of this test, plus a coordinator mutation run) that the rename is a
    // genuine rename and not a silent drop-and-add masquerading as one -- neither is optional.
    private void AssertColumnRenamedDataSurvivedAndStillInvisible(int distinguishingValue)
    {
        var oldColumnGone = Scalar($@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'secret'") == 0;
        var newColumnExists = Scalar($@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'renamed_secret'") == 1;
        var renamedIsInvisible = (ScalarStr($@"SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'renamed_secret'") ?? "")
            .Contains("INVISIBLE", StringComparison.OrdinalIgnoreCase);
        var survivingValue = Scalar($"SELECT renamed_secret FROM `{_testDb}`.`{TableName}` WHERE id = 1");
        var renameMessageLogged = Scalar($@"SELECT COUNT(*) FROM SchemaSmith_StatusMessages
           WHERE Message LIKE '%Rename column%secret%renamed_secret%{TableName}%'") >= 1;

        Assert.Multiple(() =>
        {
            Assert.That(oldColumnGone, Is.True, "The old column name must no longer exist -- true either way (rename or drop-and-add), not itself discriminating.");
            Assert.That(newColumnExists, Is.True, "The new column name must exist -- true either way (rename or drop-and-add), not itself discriminating.");
            // The decisive assertion. Do NOT weaken this to make a test pass -- if it fails, the
            // rename-band behavior is genuinely lossy or the rename was never recognised, and that is
            // exactly the fact this check exists to surface.
            Assert.That(survivingValue, Is.EqualTo(distinguishingValue),
                "The pre-existing row's value must survive the rename. If it reads back as the column's DEFAULT instead, " +
                "the column was dropped and re-added rather than renamed -- data loss, not merely a visibility question.");
            Assert.That(renameMessageLogged, Is.True,
                "SchemaSmith_MissingTableAndColumnQuench's real-path rename status message ('Rename column ... to ...') " +
                "only fires when the old-column-present/new-column-absent rename predicate matched -- its absence would mean " +
                "the rename code path was never taken at all, independent of what happened to the data or the visibility.");
            Assert.That(renamedIsInvisible, Is.True, "The renamed column must still be invisible after the same deploy that renamed it.");
        });
    }

    // RENAME COLUMN arrives at 10.5.2. A newer server is told 10.5 to reach the fallback; a real one below is already in
    // the band, and telling it 10.5 would claim catalog it lacks (CHECK_CONSTRAINTS.LEVEL, read from 10.5).
    private void SimulateRenameColumnFallbackBand()
    {
        if (Scalar("SELECT SchemaSmith_ServerVersionPatchNum()") >= 100502)
            SetVersionOverride(1005);
    }

    [Test]
    public void RenameOfInvisibleColumnWithDefault_InRenameColumnFallbackBand_StaysInvisible()
    {
        // @schemasmith_version_override = 1005 reproduces the rename-fallback / invisible-column-live
        // band on the real 11.4 container: SupportsRenameColumn() -> 0, SupportsInvisibleColumn() -> 1.
        // This is the realistic NOT NULL + DEFAULT shape (MariaDB requires a default on a NOT NULL
        // invisible column -- see NotNullInvisibleColumnWithoutDefault_IsRejectedByEngine). Proves the
        // end-to-end observable behaviour is correct for the shape actually used elsewhere in this file;
        // does not by itself prove the visibility predicate specifically did the restoring (see the
        // class-level comment above, and the nullable/no-default test below).
        if (Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()") == 0)
            Assert.Ignore("Target does not support invisible columns (MariaDB < 10.3); the rename-band scenario cannot be exercised.");

        SetPolicy("warn");

        // Step 1: create the column invisible at the real (unoverridden) version, then write a
        // distinguishing value into it directly (explicit column name -- invisible only hides a column
        // from SELECT * / unqualified INSERT, not from an explicit reference).
        Deploy(invisible: true);
        Assert.That(ColumnIsInvisible(), Is.True, "Sanity: column starts invisible.");
        Exec($"INSERT INTO `{_testDb}`.`{TableName}` (secret) VALUES (424242)");
        Assert.That(Scalar($"SELECT secret FROM `{_testDb}`.`{TableName}` WHERE id = 1"), Is.EqualTo(424242),
            "Sanity: the distinguishing value must be readable by explicit name before the rename.");

        // Clear prior StatusMessages noise so the rename-message check below is unambiguous.
        Exec($"DELETE FROM SchemaSmith_StatusMessages WHERE Message LIKE '%{TableName}%'");

        // Step 2: redeploy with the column renamed, under the override that lands exactly in the
        // rename-fallback / invisible-column-live band.
        SimulateRenameColumnFallbackBand();
        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT SchemaSmith_SupportsRenameColumn()"), Is.EqualTo(0),
                "Sanity: the override must land below the RENAME COLUMN floor so the CHANGE COLUMN fallback fires.");
            Assert.That(Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()"), Is.EqualTo(1),
                "Sanity: the override must land at/above the invisible-column floor.");
        });

        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`renamed_secret`", DataType = "INT", Nullable = false, Invisible = true, Default = "0", OldName = "secret" }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        var json = ("[" + JsonConvert.SerializeObject(table) + "]").Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('InvisibleColGateProductMdb', '{_testDb}', '{json}', 0, 0, 0)");

        AssertColumnRenamedDataSurvivedAndStillInvisible(424242);
    }

    [Test]
    public void RenameOfInvisibleColumnNullableNoDefault_InRenameColumnFallbackBand_VisibilityPredicateAloneRestoresInvisible()
    {
        // Isolates the visibility predicate. A nullable column with no DEFAULT declared on either side
        // removes the DEFAULT drift signal entirely: after CHANGE COLUMN, the physical and declared
        // definitions differ in nothing but INVISIBLE (type, nullability, charset/collate, generated,
        // auto_increment, and DEFAULT all already match), so ONLY ModifiedTableQuench's visibility-drift
        // predicate (isc.EXTRA vs c.IsInvisible) can restore it. Legal on both engines: MariaDB's
        // NOT-NULL-invisible-needs-a-default rule does not apply to a nullable column.
        if (Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()") == 0)
            Assert.Ignore("Target does not support invisible columns (MariaDB < 10.3); the rename-band scenario cannot be exercised.");

        SetPolicy("warn");

        var initial = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`secret`", DataType = "INT", Nullable = true, Invisible = true }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        Exec($"CALL SchemaSmith_TableQuench('InvisibleColGateProductMdb', '{_testDb}', '{("[" + JsonConvert.SerializeObject(initial) + "]").Replace("'", "''")}', 0, 0, 0)");
        Assert.That(ColumnIsInvisible(), Is.True, "Sanity: column starts invisible.");
        Exec($"INSERT INTO `{_testDb}`.`{TableName}` (secret) VALUES (424242)");
        Assert.That(Scalar($"SELECT secret FROM `{_testDb}`.`{TableName}` WHERE id = 1"), Is.EqualTo(424242),
            "Sanity: the distinguishing value must be readable by explicit name before the rename.");

        Exec($"DELETE FROM SchemaSmith_StatusMessages WHERE Message LIKE '%{TableName}%'");

        SimulateRenameColumnFallbackBand();
        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT SchemaSmith_SupportsRenameColumn()"), Is.EqualTo(0),
                "Sanity: the override must land below the RENAME COLUMN floor so the CHANGE COLUMN fallback fires.");
            Assert.That(Scalar("SELECT SchemaSmith_SupportsInvisibleColumn()"), Is.EqualTo(1),
                "Sanity: the override must land at/above the invisible-column floor.");
        });

        var renamed = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`renamed_secret`", DataType = "INT", Nullable = true, Invisible = true, OldName = "secret" }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        Exec($"CALL SchemaSmith_TableQuench('InvisibleColGateProductMdb', '{_testDb}', '{("[" + JsonConvert.SerializeObject(renamed) + "]").Replace("'", "''")}', 0, 0, 0)");

        AssertColumnRenamedDataSurvivedAndStillInvisible(424242);
    }
}
