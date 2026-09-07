// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// InnoDB DATA DIRECTORY placement (F2c), MariaDB side: a table's declared <c>DataDirectory</c> is applied
/// as <c>DATA DIRECTORY='&lt;path&gt;'</c> on CREATE (<c>SchemaSmith_MissingTableAndColumnQuench</c>), and
/// on an EXISTING table a declared value that disagrees with what is deployed is REFUSED rather than moved
/// (<c>SchemaSmith_ModifiedTableQuench</c>). Unlike Tablespace (F2b, MySQL-only), this property applies to
/// BOTH engines -- MariaDB has no general tablespaces, but it DOES support DATA DIRECTORY.
/// <para><b>LOCAL-ONLY sweep, not the normal gate.</b> The demo/CI MariaDB container has no
/// <c>/ddspace</c> directory owned by <c>mysql</c>, so these tests need the purpose-built image
/// <c>scripts/test-infra/datadir/mariadb</c>, run via <c>scripts/run-datadir-sweep.sh</c> -- the same
/// reason the Encryption category is a separate sweep rather than part of the normal gate.</para>
/// <para>Outcome, not mechanism (Rule 32): the assertions read
/// <c>INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS</c> directly -- the exact catalog source
/// <c>SchemaSmith_TableDataDirectory</c>'s MariaDb body itself parses -- rather than trusting the emitted
/// CREATE TABLE SQL, so a placement bug that still produces a plausible-looking statement cannot pass
/// silently.</para>
/// </summary>
[Category("MariaDb")]
[Category("DataDirectory")]
[TestFixture]
public class DataDirectoryQuenchTests
{
    private const string TableName = "mdb_datadir_test";
    private const string ProbeTable = "mdb_datadir_probe";
    private const string DataDir = "/ddspace";
    // The refuse test needs a SECOND, different declared value that need not exist -- the refuse fires
    // from the declared-vs-deployed diff BEFORE any CREATE/ALTER is attempted (verified below).
    private const string UnknownDataDir = "/ddspace-does-not-exist";

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        // Availability gate (mirrors the encryption fixture's keyring gate): these tests need a
        // mysql-writable /ddspace directory that ONLY the purpose-built datadir image provides
        // (scripts/run-datadir-sweep.sh). On a stock container -- the normal gate, the version-floor sweep --
        // it is absent, so DATA DIRECTORY='/ddspace' cannot be created. Probe it and Assert.Ignore the whole
        // fixture when unavailable, rather than failing tests for missing local-only infra.
        try
        {
            Exec($"CREATE TABLE `{_testDb}`.`{ProbeTable}` (id INT) DATA DIRECTORY='{DataDir}'");
            Exec($"DROP TABLE `{_testDb}`.`{ProbeTable}`");
        }
        catch (System.Data.Common.DbException ex)
        {
            Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{ProbeTable}`");
            Assert.Ignore($"DATA DIRECTORY='{DataDir}' is not usable on this server -- the purpose-built "
                          + "/ddspace directory is absent. Run scripts/run-datadir-sweep.sh, which provisions it. "
                          + $"Probe failed: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
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

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private void Deploy(string extraProps, int whatIf = 0)
    {
        var json = "[{ \"Name\": \"`" + TableName + "`\", \"Engine\": \"InnoDB\"" + extraProps
                   + ", \"Columns\": [ { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false } ],"
                   + " \"Indexes\": [ { \"Name\": \"`pk_" + TableName + "`\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"`id`\" } ] }]";
        Exec($"CALL SchemaSmith_TableQuench('MdbDataDirectoryProduct', '{_testDb}', '{json.Replace("'", "''")}', {whatIf}, 0, 0)");
    }

    private string ExtractedJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')";
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? "" : r.ToString();
    }

    // The exact source SchemaSmith_TableDataDirectory's MariaDb body parses: CREATE_OPTIONS, a single
    // free-text catalog column. Queried directly here, independent of the production parser, so a
    // placement bug that still produces plausible-looking DDL cannot pass silently.
    private string DeployedCreateOptions() => ScalarStr(
        $"SELECT CREATE_OPTIONS FROM INFORMATION_SCHEMA.TABLES "
        + $"WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'");

    [Test]
    public void DataDirectory_IsAppliedAndRoundTrips()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");

        Assert.Multiple(() =>
        {
            // The outcome: the table is actually placed in the declared directory -- MariaDB canonicalizes
            // CREATE_OPTIONS with a trailing slash, which is the physical-placement evidence, independent
            // of the production parser under test.
            Assert.That(DeployedCreateOptions(), Does.Contain($"DATA DIRECTORY='{DataDir}/'"), DeployedCreateOptions());
            // Round-trip: a re-extraction declares the same placement, with the trailing slash stripped so
            // it matches what the user actually declared.
            Assert.That(ExtractedJson(), Does.Contain("\"DataDirectory\""), ExtractedJson());
            Assert.That(ExtractedJson(), Does.Contain($"\"{DataDir}\""), ExtractedJson());
            Assert.That(ExtractedJson(), Does.Not.Contain($"{DataDir}/\""), ExtractedJson());
        });
    }

    [Test]
    public void ATableDeclaringNone_ExtractsWithoutTheKey()
    {
        // No-churn contract: a table with no declared placement (the overwhelming majority) must extract
        // exactly as it did before this feature shipped, and CREATE_OPTIONS must carry no DATA DIRECTORY
        // clause at all.
        Deploy("");

        Assert.Multiple(() =>
        {
            Assert.That(DeployedCreateOptions() ?? "", Does.Not.Contain("DATA DIRECTORY"), DeployedCreateOptions());
            Assert.That(ExtractedJson(), Does.Not.Contain("DataDirectory"), ExtractedJson());
        });
    }

    [Test]
    public void RedeployingToADifferentDirectory_IsRefusedAndTheTableDoesNotMove()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");
        var placedOptions = DeployedCreateOptions();
        Assert.That(placedOptions, Does.Contain($"DATA DIRECTORY='{DataDir}/'"), "sanity: initial placement must succeed");

        Assert.That(() => Deploy($", \"DataDirectory\": \"{UnknownDataDir}\""),
            Throws.Exception.With.Message.Contains("data directory"),
            "a declared data-directory change must be refused by name, not silently applied as a move");

        // The outcome that matters: refused means UNCHANGED, not a partial/best-effort move -- and this
        // holds even though UnknownDataDir was never created, proving the refuse is a pre-check that fires
        // BEFORE any CREATE/ALTER is attempted against it.
        Assert.That(DeployedCreateOptions(), Is.EqualTo(placedOptions),
            "the table must still be in its original directory after the refused redeploy");
    }

    [Test]
    public void RedeployingToADifferentDirectory_IsRefusedUnderWhatIfToo()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");
        var placedOptions = DeployedCreateOptions();

        Assert.That(() => Deploy($", \"DataDirectory\": \"{UnknownDataDir}\"", whatIf: 1),
            Throws.Exception.With.Message.Contains("data directory"),
            "the refuse has no safe WhatIf preview -- it must abort in both modes");

        Assert.That(DeployedCreateOptions(), Is.EqualTo(placedOptions),
            "a WhatIf run must never move the table either");
    }

    [Test]
    public void RedeployingTheSameDirectory_IsANoOp()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");

        Assert.DoesNotThrow(() => Deploy($", \"DataDirectory\": \"{DataDir}\""));
        Assert.That(DeployedCreateOptions(), Does.Contain($"DATA DIRECTORY='{DataDir}/'"));
    }
}
