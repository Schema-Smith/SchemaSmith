// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// A declared application-time period has to reach the database.
/// <para>Extraction learned to read periods before deploy learned to write them, and that gap is the
/// dangerous shape: a package extracted from an 11.4 server carries its periods, and deploying it to a
/// fresh database would create the table without them — losing a declared part of the schema with no
/// error and nothing in the output to notice. This is the test that says the round trip closes.</para>
/// <para>MariaDB-only: MySQL has no application-time periods at any version.</para>
/// </summary>
[Category("MariaDb")]
[TestFixture]
public class ApplicationTimePeriodDeployTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDbName => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();

    private const string TableName = "PeriodDeployTarget";

    private static string TableJson() => $$"""
        [{
            "Name": "{{TableName}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "ValidFrom", "DataType": "DATE", "Nullable": false },
                { "Name": "ValidTo", "DataType": "DATE", "Nullable": false }
            ],
            "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ],
            "Periods": [
                { "Name": "Validity", "StartColumn": "ValidFrom", "EndColumn": "ValidTo" }
            ]
        }]
        """;

    [Test]
    public void ADeclaredPeriodIsCreatedWithTheTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_SupportsApplicationTimePeriods()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("This server cannot declare an application-time period (MariaDB 10.4.3+), so the "
                          + "clause is deliberately suppressed and there is nothing to verify.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();

        RunTableQuenchProc(cmd, TableJson());

        // Asserted against the catalog rather than against the generated SQL: the point is that the
        // period EXISTS on the deployed table, not that a particular statement was emitted. Reading
        // PERIODS needs 11.4 even though declaring one needs only 10.4.3 -- the two thresholds are
        // genuinely different, so on 10.4.3-11.3 fall back to proving the table deployed at all.
        cmd.CommandText = "SELECT SchemaSmith_ServerVersionNum()";
        var version = Convert.ToInt32(cmd.ExecuteScalar());

        if (version < 1104)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                              + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{TableName}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                "Below 11.4 the period cannot be read back, but the table carrying it must still deploy — "
                + "a period clause the engine accepts must not break the CREATE.");
            Assert.Ignore($"MariaDB {version} has no INFORMATION_SCHEMA.PERIODS (11.4+), so the period "
                          + "itself cannot be verified here. The table deployed.");
        }

        cmd.CommandText = $@"
SELECT CONCAT(PERIOD, '|', START_COLUMN_NAME, '|', END_COLUMN_NAME)
  FROM INFORMATION_SCHEMA.PERIODS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{TableName}' AND PERIOD <> 'SYSTEM_TIME'";
        var actual = cmd.ExecuteScalar()?.ToString();

        Assert.That(actual, Is.EqualTo("Validity|ValidFrom|ValidTo"),
            "The declared period must reach the database with its columns in the declared order. A "
            + "transposed pair would invert every interval the table describes.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    /// <summary>
    /// A period declared for a table that ALREADY exists is added to it, and declaring it again does
    /// nothing.
    /// <para>The idempotence half is the one that bites: `ADD PERIOD FOR` fails outright if the period
    /// is already there, so a convergence pass that cannot tell would break every re-deploy. That is
    /// exactly why this is gated on 11.4 rather than on 10.4.3 — declaring a period needs 10.4.3, but
    /// knowing whether one is already present needs the catalog that arrives in 11.4.</para>
    /// </summary>
    [Test]
    public void APeriodIsAddedToAnExistingTable_AndReDeployingIsANoOp()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_ServerVersionNum()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 1104)
            Assert.Ignore("Reconciling a period on an existing table needs MariaDB 11.4 to read the "
                          + "current state; below that it is declined by design.");

        const string existing = "PeriodAddedLater";
        cmd.CommandText = $"DROP TABLE IF EXISTS `{existing}`";
        cmd.ExecuteNonQuery();

        // Deployed WITHOUT a period first, so the period genuinely has to be added by a later pass
        // rather than riding the CREATE.
        cmd.CommandText = $@"
CREATE TABLE `{existing}` (`Id` INT NOT NULL PRIMARY KEY,
                           `ValidFrom` DATE NOT NULL, `ValidTo` DATE NOT NULL) ENGINE=InnoDB";
        cmd.ExecuteNonQuery();

        var json = $$"""
            [{
                "Name": "{{existing}}",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "ValidFrom", "DataType": "DATE", "Nullable": false },
                    { "Name": "ValidTo", "DataType": "DATE", "Nullable": false }
                ],
                "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ],
                "Periods": [
                    { "Name": "Validity", "StartColumn": "ValidFrom", "EndColumn": "ValidTo" }
                ]
            }]
            """;

        RunTableQuenchProc(cmd, json);

        cmd.CommandText = $@"
SELECT CONCAT(PERIOD, '|', START_COLUMN_NAME, '|', END_COLUMN_NAME)
  FROM INFORMATION_SCHEMA.PERIODS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{existing}' AND PERIOD <> 'SYSTEM_TIME'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Validity|ValidFrom|ValidTo"),
            "A period declared for an existing table must be added to it.");

        // The second pass is the real assertion. ADD PERIOD FOR errors if the period exists, so a
        // convergence pass that cannot see the current state would throw here on every re-deploy.
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json),
            "Re-deploying an unchanged package must be a no-op. ADD PERIOD FOR fails when the period is "
            + "already present, so this throwing means the existence check is not working.");

        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.PERIODS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{existing}' AND PERIOD <> 'SYSTEM_TIME'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
            "and the re-deploy must not have duplicated it.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{existing}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }


    private const string DropTarget = "PeriodDropTarget";

    /// <summary>Package for <see cref="DropTarget"/>, with its single period or with none.</summary>
    /// <remarks>MariaDB permits at most ONE application-time period per table (error 4154), so the
    /// choice is "the period" or "no periods" -- there is no partial case to test.</remarks>
    private static string DropTargetJson(bool withPeriod) => $$"""
        [{
            "Name": "{{DropTarget}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "ValidFrom", "DataType": "DATE", "Nullable": false },
                { "Name": "ValidTo", "DataType": "DATE", "Nullable": false }
            ],
            "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ],
            "Periods": [{{(withPeriod ? "{ \"Name\": \"Validity\", \"StartColumn\": \"ValidFrom\", \"EndColumn\": \"ValidTo\" }" : "")}}]
        }]
        """;

    private int PeriodCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.PERIODS "
                          + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND PERIOD <> 'SYSTEM_TIME'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool SkipUnlessPeriodsReadable(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT SchemaSmith_ServerVersionNum()";
        return Convert.ToInt32(cmd.ExecuteScalar()) < 1104;
    }

    /// <summary>
    /// A period on the table but no longer in the package is dropped — but ONLY when asked for.
    /// <para>The off-by-default half is the whole safety argument and is asserted first. Extraction omits
    /// the <c>Periods</c> key when a table has none, so a package written before periods were supported,
    /// or extracted from MariaDB 10.4.3–11.3 where the catalog cannot report them, carries no periods
    /// even when the table has one. Dropping on that absence would remove a declaration the package
    /// never had the chance to make — so the default has to be pinned, not assumed.</para>
    /// </summary>
    [Test]
    public void APeriodRemovedFromThePackageIsDropped_OnlyWhenTheFlagIsOn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (SkipUnlessPeriodsReadable(cmd))
            Assert.Ignore("Deciding what to drop needs MariaDB 11.4 to read the current periods.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{DropTarget}`";
        cmd.ExecuteNonQuery();

        // Deploy WITH both periods, flag irrelevant on a create.
        cmd.CommandText = "SET @ss_drop_periods_removed = 0";
        cmd.ExecuteNonQuery();
        RunTableQuenchProc(cmd, DropTargetJson(withPeriod: true));
        Assert.That(PeriodCount(cmd, DropTarget), Is.EqualTo(1), "setup: the period must be created");

        // Now remove one from the package with the flag OFF -- it must survive.
        RunTableQuenchProc(cmd, DropTargetJson(withPeriod: false));
        Assert.That(PeriodCount(cmd, DropTarget), Is.EqualTo(1),
            "With the flag off, a period missing from the package must be LEFT ALONE. This is the "
            + "default, and it is what stops a package that predates periods -- or was extracted below "
            + "11.4 and so carries none -- from silently deleting periods it never had the chance to "
            + "declare.");

        // And with the variable never set at all -- the state a direct CALL leaves it in, and the real
        // default. Asserted separately because setting it to 0 exercises a DIFFERENT branch: the
        // COALESCE fallback only applies when the variable is NULL, so a test that always assigns it
        // cannot detect the default being flipped. Found exactly that way -- flipping the fallback to 1
        // left the earlier assertion green.
        cmd.CommandText = "SET @ss_drop_periods_removed = NULL";
        cmd.ExecuteNonQuery();
        RunTableQuenchProc(cmd, DropTargetJson(withPeriod: false));
        Assert.That(PeriodCount(cmd, DropTarget), Is.EqualTo(1),
            "With the variable UNSET, the period must still be left alone. Unset is what a caller that has never heard of this flag leaves behind, and it must mean off.");

        // Same package, flag ON -- now it goes.
        cmd.CommandText = "SET @ss_drop_periods_removed = 1";
        cmd.ExecuteNonQuery();
        RunTableQuenchProc(cmd, DropTargetJson(withPeriod: false));

        Assert.That(PeriodCount(cmd, DropTarget), Is.Zero,
            "with the flag on, the period the package no longer declares must be dropped");
        // Not data-destructive: the columns the period spanned are still there.
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                          + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{DropTarget}'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3),
            "dropping a period must not take its columns with it -- this is what separates 'removed the "
            + "period' from 'removed the data'");

        // And re-running is a no-op rather than an error on an already-absent period.
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, DropTargetJson(withPeriod: false)),
            "re-deploying after the drop must not try to drop it again");
        Assert.That(PeriodCount(cmd, DropTarget), Is.Zero);

        cmd.CommandText = "SET @ss_drop_periods_removed = 0";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DROP TABLE IF EXISTS `{DropTarget}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

}
