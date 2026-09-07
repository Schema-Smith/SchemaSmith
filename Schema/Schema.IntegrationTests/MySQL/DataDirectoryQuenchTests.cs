// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// InnoDB DATA DIRECTORY placement (F2c), MySQL side: a table's declared <c>DataDirectory</c> is applied
/// as <c>DATA DIRECTORY='&lt;path&gt;'</c> on CREATE (<c>SchemaSmith_MissingTableAndColumnQuench</c>), and
/// on an EXISTING table a declared value that disagrees with what is deployed is REFUSED rather than moved
/// (<c>SchemaSmith_ModifiedTableQuench</c>) -- the same placement posture Tablespace (F2b) takes.
/// <para><b>LOCAL-ONLY sweep, not the normal gate.</b> The demo/CI MySQL container has no <c>/ddspace</c>
/// directory listed in <c>innodb_directories</c>, so these tests need the purpose-built image
/// <c>scripts/test-infra/datadir/mysql</c>, run via <c>scripts/run-datadir-sweep.sh</c> -- the same reason
/// the Encryption category is a separate sweep rather than part of the normal gate.</para>
/// <para>Outcome, not mechanism (Rule 32): the assertions read
/// <c>INFORMATION_SCHEMA.INNODB_DATAFILES.PATH</c> directly -- the exact catalog source
/// <c>SchemaSmith_TableDataDirectory</c> itself reads -- rather than trusting the emitted CREATE TABLE SQL,
/// so a placement bug that still produces a plausible-looking statement cannot pass silently.</para>
/// </summary>
[Category("MySQL")]
[Category("DataDirectory")]
[TestFixture]
public class DataDirectoryQuenchTests
{
    private const string TableName = "datadir_quench_test";
    private const string ProbeTable = "datadir_probe";
    private const string DataDir = "/ddspace";
    // The refuse test needs a SECOND, different declared value -- it must not exist / not be a known
    // innodb_directories entry, because the refuse fires from the declared-vs-deployed diff BEFORE any
    // CREATE/ALTER is attempted (verified by the "does not move" assertion below), so this path is never
    // actually created against.
    private const string UnknownDataDir = "/ddspace-does-not-exist";

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        // Availability gate (mirrors the encryption fixture's keyring gate): these tests need a
        // mysql-writable /ddspace directory listed in innodb_directories, which ONLY the purpose-built
        // datadir image provides (scripts/run-datadir-sweep.sh). On a stock container -- the normal gate, the
        // version-floor sweep -- it is absent (and 5.7/8.0 stock reject DATA DIRECTORY='/ddspace'), so probe
        // it and Assert.Ignore the whole fixture when unavailable rather than failing on missing local infra.
        try
        {
            Exec($"CREATE TABLE `{_testDb}`.`{ProbeTable}` (id INT) DATA DIRECTORY='{DataDir}'");
            Exec($"DROP TABLE `{_testDb}`.`{ProbeTable}`");
        }
        catch (System.Data.Common.DbException ex)
        {
            Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{ProbeTable}`");
            Assert.Ignore($"DATA DIRECTORY='{DataDir}' is not usable on this server -- the purpose-built "
                          + "/ddspace directory (+ innodb_directories) is absent. Run scripts/run-datadir-sweep.sh, "
                          + $"which provisions it. Probe failed: {ex.Message}");
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
        Exec($"CALL SchemaSmith_TableQuench('DataDirectoryProduct', '{_testDb}', '{json.Replace("'", "''")}', {whatIf}, 0, 0)");
    }

    private string ExtractedJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')";
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? "" : r.ToString();
    }

    // The exact source SchemaSmith_TableDataDirectory reads on MySQL (via dynamic SQL -- see that script
    // for why it's a procedure, not a function): INNODB_DATAFILES joined to INNODB_TABLES by SPACE.
    // Queried directly here with plain (non-dynamic) SQL -- fine on this fixture's 8.0 container, where
    // the unprefixed views exist unconditionally. Asserting against this catalog read -- the table's
    // actual physical data-file path -- not the emitted CREATE TABLE SQL, is the outcome that matters.
    private string DeployedPath() => ScalarStr(
        $"SELECT df.PATH FROM INFORMATION_SCHEMA.INNODB_DATAFILES df "
        + $"JOIN INFORMATION_SCHEMA.INNODB_TABLES it ON it.SPACE = df.SPACE "
        + $"WHERE it.NAME = '{_testDb}/{TableName}'");

    [Test]
    public void DataDirectory_IsAppliedAndRoundTrips()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");

        Assert.Multiple(() =>
        {
            // The outcome: the table's data file is actually physically placed under the declared
            // directory -- read straight off the catalog's file path, not the emitted DDL text.
            Assert.That(DeployedPath(), Does.StartWith(DataDir + "/"), DeployedPath());
            // Round-trip: a re-extraction declares the same placement, with no trailing slash even though
            // MySQL's own PATH derivation never had one to strip in the first place.
            Assert.That(ExtractedJson(), Does.Contain("\"DataDirectory\""), ExtractedJson());
            Assert.That(ExtractedJson(), Does.Contain($"\"{DataDir}\""), ExtractedJson());
        });
    }

    [Test]
    public void ATableDeclaringNone_ExtractsWithoutTheKey()
    {
        // No-churn contract: a table with no declared placement (the overwhelming majority) must extract
        // exactly as it did before this feature shipped, and its data file must sit in the server's
        // default datadir -- a relative PATH, never an absolute one.
        Deploy("");

        Assert.Multiple(() =>
        {
            Assert.That(DeployedPath(), Does.StartWith("./"), DeployedPath());
            Assert.That(ExtractedJson(), Does.Not.Contain("DataDirectory"), ExtractedJson());
        });
    }

    [Test]
    public void RedeployingToADifferentDirectory_IsRefusedAndTheTableDoesNotMove()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");
        var placedPath = DeployedPath();
        Assert.That(placedPath, Does.StartWith(DataDir + "/"), "sanity: initial placement must succeed");

        Assert.That(() => Deploy($", \"DataDirectory\": \"{UnknownDataDir}\""),
            Throws.Exception.With.Message.Contains("data directory"),
            "a declared data-directory change must be refused by name, not silently applied as a move");

        // The outcome that matters: refused means UNCHANGED, not a partial/best-effort move -- and this
        // holds even though UnknownDataDir was never created, proving the refuse is a pre-check that fires
        // BEFORE any CREATE/ALTER is attempted against it.
        Assert.That(DeployedPath(), Is.EqualTo(placedPath),
            "the table's data file must still be in its original directory after the refused redeploy");
    }

    [Test]
    public void RedeployingToADifferentDirectory_IsRefusedUnderWhatIfToo()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");
        var placedPath = DeployedPath();

        Assert.That(() => Deploy($", \"DataDirectory\": \"{UnknownDataDir}\"", whatIf: 1),
            Throws.Exception.With.Message.Contains("data directory"),
            "the refuse has no safe WhatIf preview -- it must abort in both modes");

        Assert.That(DeployedPath(), Is.EqualTo(placedPath),
            "a WhatIf run must never move the table either");
    }

    [Test]
    public void RedeployingTheSameDirectory_IsANoOp()
    {
        Deploy($", \"DataDirectory\": \"{DataDir}\"");

        Assert.DoesNotThrow(() => Deploy($", \"DataDirectory\": \"{DataDir}\""));
        Assert.That(DeployedPath(), Does.StartWith(DataDir + "/"));
    }
}
