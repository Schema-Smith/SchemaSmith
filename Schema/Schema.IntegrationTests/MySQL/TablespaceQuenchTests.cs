// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// MySQL general-tablespace placement (F2b): a table's declared <c>Tablespace</c> is applied as
/// <c>TABLESPACE &lt;name&gt;</c> on CREATE (<c>SchemaSmith_MissingTableAndColumnQuench</c>), and on an
/// EXISTING table a declared value that disagrees with what is deployed is REFUSED rather than moved
/// (<c>SchemaSmith_ModifiedTableQuench</c> STEP -0.4) -- placement, like partitioning, is applied once and
/// never migrated by a state diff.
/// <para><b>Tablespace cleanup is mandatory.</b> A general tablespace is a SERVER-GLOBAL object that
/// outlives any one test database and persists across tables -- a general tablespace left behind after a
/// run pollutes the shared container for every later run against this server. The fixture creates two
/// fixed-name tablespaces in <see cref="OneTimeSetUp"/> and DROPs both (and the test table, which must go
/// first -- a tablespace cannot be dropped while a table still references it) in
/// <see cref="OneTimeTearDown"/>.</para>
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class TablespaceQuenchTests
{
    private const string TableName = "tablespace_quench_test";
    private const string Tablespace1 = "ss_test_tablespace_1";
    private const string Tablespace2 = "ss_test_tablespace_2";

    private IDbConnection _connection = null!;
    private bool _sysViews;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        // 5.7 keeps the placement facts under the SYS_-prefixed view names; the reader and this fixture's own
        // catalog read both pick the names by version.
        var version = ScalarStr("SELECT VERSION()") ?? "";
        _sysViews = int.TryParse(version.Split('.')[0], out var m) && m < 8;

        // Defensive: a prior aborted run could have left either object behind.
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
        DropTablespaceIfExists(Tablespace1);
        DropTablespaceIfExists(Tablespace2);

        Exec($"CREATE TABLESPACE {Tablespace1} ADD DATAFILE '{Tablespace1}.ibd' ENGINE=InnoDB");
        Exec($"CREATE TABLESPACE {Tablespace2} ADD DATAFILE '{Tablespace2}.ibd' ENGINE=InnoDB");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        // The table MUST go before the tablespaces -- MySQL refuses to DROP TABLESPACE while any table
        // still references it.
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
        DropTablespaceIfExists(Tablespace1);
        DropTablespaceIfExists(Tablespace2);
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    // ---- helpers -----------------------------------------------------------

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void DropTablespaceIfExists(string name)
    {
        try
        {
            Exec($"DROP TABLESPACE {name}");
        }
        catch (MySqlException)
        {
            // Did not exist -- fine, this is a defensive cleanup, not an assertion. Narrowed to the
            // engine's own error: a connection or fixture failure here should surface, not be swallowed
            // by a cleanup helper and reported as a passing test.
        }
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private void Deploy(string extraProps, int whatIf = 0, string table = TableName)
    {
        var json = "[{ \"Name\": \"`" + table + "`\", \"Engine\": \"InnoDB\"" + extraProps
                   + ", \"Columns\": [ { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false } ],"
                   + " \"Indexes\": [ { \"Name\": \"`pk_" + table.Replace("-", "_") + "`\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"`id`\" } ] }]";
        Exec($"CALL SchemaSmith_TableQuench('TablespaceProduct', '{_testDb}', '{json.Replace("'", "''")}', {whatIf}, 0, 0)");
    }

    private string ExtractedJson(string table = TableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{table}')";
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? "" : r.ToString();
    }

    // The exact source SchemaSmith_TableTablespace reads (via dynamic SQL on the MySQL side -- see that
    // script for why it's a procedure, not a function): INNODB_TABLES joined to INNODB_TABLESPACES,
    // filtered to a NAMED general tablespace (SPACE_TYPE = 'General'), never the implicit per-table form.
    // Queried directly here with plain (non-dynamic) SQL -- fine on this fixture's 8.0 container, where
    // the unprefixed views exist unconditionally. Asserting against this catalog read -- not the emitted
    // CREATE TABLE SQL -- is the outcome that actually matters: the table really is (or is not)
    // physically placed in the tablespace.
    private string DeployedTablespace() => ScalarStr(
        $"SELECT ts.NAME FROM INFORMATION_SCHEMA.{(_sysViews ? "INNODB_SYS_TABLES" : "INNODB_TABLES")} it "
        + $"JOIN INFORMATION_SCHEMA.{(_sysViews ? "INNODB_SYS_TABLESPACES" : "INNODB_TABLESPACES")} ts ON ts.SPACE = it.SPACE "
        + $"WHERE it.NAME = '{_testDb}/{TableName}' AND ts.SPACE_TYPE = 'General'");

    // InnoDB stores `ts-probe` as `ts@002dprobe`. Matching the raw name missed it, so the placement read back as none:
    // extraction dropped it, and a redeploy declaring it was refused as a move.
    [Test]
    public void ATableWhoseNameInnoDbEncodes_RoundTripsItsTablespace()
    {
        const string table = "ts-probe";
        try
        {
            Deploy($", \"Tablespace\": \"{Tablespace1}\"", table: table);
            Assert.That(ExtractedJson(table), Does.Contain(Tablespace1), ExtractedJson(table));
            Assert.DoesNotThrow(() => Deploy($", \"Tablespace\": \"{Tablespace1}\"", table: table),
                "redeploying the same placement must not be refused as a move");
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{table}`");
        }
    }

    // A table placed explicitly in the system tablespace is space 0, which has no tablespace row, so it read back as
    // unplaced and a redeploy declaring innodb_system was refused.
    [Test]
    public void ATableInTheSystemTablespace_RoundTripsIt()
    {
        Deploy(", \"Tablespace\": \"innodb_system\"");
        Assert.That(ExtractedJson(), Does.Contain("innodb_system"), ExtractedJson());
        Assert.DoesNotThrow(() => Deploy(", \"Tablespace\": \"innodb_system\""),
            "redeploying the same placement must not be refused as a move");
    }

    [Test]
    public void Tablespace_IsAppliedAndRoundTrips()
    {
        Deploy($", \"Tablespace\": \"{Tablespace1}\"");

        Assert.Multiple(() =>
        {
            // The outcome: the table is actually placed in the declared general tablespace.
            Assert.That(DeployedTablespace(), Is.EqualTo(Tablespace1));
            // Round-trip: a re-extraction declares the same placement.
            Assert.That(ExtractedJson(), Does.Contain("\"Tablespace\""), ExtractedJson());
            Assert.That(ExtractedJson(), Does.Contain(Tablespace1), ExtractedJson());
        });
    }

    [Test]
    public void ATableDeclaringNone_ExtractsWithoutTheKey()
    {
        // No-churn contract: a table in no named tablespace (the overwhelming majority) must extract
        // exactly as it did before this feature shipped.
        Deploy("");

        Assert.Multiple(() =>
        {
            Assert.That(DeployedTablespace(), Is.Null);
            Assert.That(ExtractedJson(), Does.Not.Contain("Tablespace"), ExtractedJson());
        });
    }

    [Test]
    public void RedeployingToADifferentTablespace_IsRefusedAndTheTableDoesNotMove()
    {
        Deploy($", \"Tablespace\": \"{Tablespace1}\"");
        Assert.That(DeployedTablespace(), Is.EqualTo(Tablespace1), "sanity: initial placement must succeed");

        Assert.That(() => Deploy($", \"Tablespace\": \"{Tablespace2}\""),
            Throws.Exception.With.Message.Contains("tablespace"),
            "a declared tablespace change must be refused by name, not silently applied as a move");

        // The outcome that matters: refused means UNCHANGED, not a partial/best-effort move.
        Assert.That(DeployedTablespace(), Is.EqualTo(Tablespace1),
            "the table must still be in its original tablespace after the refused redeploy");
    }

    // MySQL tablespace names are case-sensitive (CREATE TABLE ... TABLESPACE in a different case fails with 3510), so a
    // declaration differing only in case names a different tablespace and must be refused like any other.
    [Test]
    public void RedeployingToATablespaceSpelledDifferentlyInCase_IsRefused()
    {
        Deploy($", \"Tablespace\": \"{Tablespace1}\"");

        Assert.That(() => Deploy($", \"Tablespace\": \"{Tablespace1.ToUpperInvariant()}\""),
            Throws.Exception.With.Message.Contains("tablespace"));

        Assert.That(DeployedTablespace(), Is.EqualTo(Tablespace1));
    }

    [Test]
    public void RedeployingToADifferentTablespace_IsRefusedUnderWhatIfToo()
    {
        Deploy($", \"Tablespace\": \"{Tablespace1}\"");

        Assert.That(() => Deploy($", \"Tablespace\": \"{Tablespace2}\"", whatIf: 1),
            Throws.Exception.With.Message.Contains("tablespace"),
            "the refuse has no safe WhatIf preview -- it must abort in both modes");

        Assert.That(DeployedTablespace(), Is.EqualTo(Tablespace1),
            "a WhatIf run must never move the table either");
    }

    [Test]
    public void RedeployingTheSameTablespace_IsANoOp()
    {
        Deploy($", \"Tablespace\": \"{Tablespace1}\"");

        Assert.DoesNotThrow(() => Deploy($", \"Tablespace\": \"{Tablespace1}\""));
        Assert.That(DeployedTablespace(), Is.EqualTo(Tablespace1));
    }
}
