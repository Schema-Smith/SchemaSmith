// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// At-rest table encryption (F2a) on MySQL: <c>ENCRYPTION='Y'</c>, applied on CREATE
/// (<c>SchemaSmith_MissingTableAndColumnQuench</c>) and converged on an existing table
/// (<c>SchemaSmith_ModifiedTableQuench</c> STEP 6.5).
/// <para><b>Why this is [Explicit] and does not run in CI or a normal local run.</b> MySQL's
/// <c>component_keyring_file</c> plugin will not initialize inside the stock Oracle <c>mysql:8.0</c>
/// image's entrypoint (Component_status stays <c>Disabled</c>; MySQL bug #108197 family), so
/// <c>ENCRYPTION='Y'</c> fails with "Can't find master key from keyring" against every MySQL container
/// this repo currently builds -- the demo containers, the CI matrix, and
/// <c>scripts/run-encryption-sweep.sh</c> (which is MariaDB-only for exactly this reason; see
/// <c>Schema.IntegrationTests.MariaDb.EncryptionQuenchTests</c> and
/// <c>scripts/test-infra/encryption/README.md</c>). Until a custom-entrypoint workaround or a pre-baked
/// keyring image lands there, this test cannot be automated -- it is left here, [Explicit] and
/// self-documenting, as the place a future keyring-capable MySQL run would slot in.</para>
/// <para>The emit/parse/converge CODE is engine-symmetric with the MariaDB path (same three touchpoints,
/// same <c>SchemaSmith_CreateOption</c> reader over <c>CREATE_OPTIONS</c>) and is covered by the MariaDB
/// integration tests plus review meanwhile.</para>
/// </summary>
[Explicit("MySQL at-rest encryption needs component_keyring_file, which does not initialize inside the "
           + "stock Oracle mysql:8.0 image (bug #108197 family) -- see scripts/test-infra/encryption/README.md. "
           + "Run manually against a MySQL instance with a working keyring.")]
[TestFixture]
public class EncryptionQuenchTests
{
    private const string TableName = "mysql_encryption_quench_test";

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        // Probe rather than assume: attempt the plain DDL this feature relies on, and Ignore (not Fail)
        // when the target has no working keyring -- an explicit run against the wrong target should say
        // why, not look broken. See the class doc for why this fails against every container in this repo
        // today.
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
        try
        {
            Exec($"CREATE TABLE `{_testDb}`.`{TableName}` (id INT PRIMARY KEY) ENGINE=InnoDB ENCRYPTION='Y'");
        }
        catch (MySqlException ex)
        {
            // Narrow deliberately: a keyring-less server rejects this DDL with a MySqlException, and that
            // is the only case worth turning into an Ignore. Catching Exception here would also swallow a
            // broken connection or a bad fixture and report it as "encryption unavailable" -- a green run
            // hiding a real failure, which is the whole reason to avoid a bare catch.
            Assert.Ignore("MySQL encryption is unavailable on this target (no working keyring): " + ex.Message);
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
        _connection?.Close();
        _connection?.Dispose();
    }

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

    private string CreateOptions() => ScalarStr(
        $"SELECT COALESCE(CREATE_OPTIONS, '') FROM INFORMATION_SCHEMA.TABLES "
        + $"WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'") ?? "";

    [Test]
    public void Encryption_IsAppliedAndVisibleInCreateOptions()
    {
        // OneTimeSetUp already proved the target has a working keyring (or this test never runs), so
        // CREATE_OPTIONS for the table it created there must show what was declared.
        Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain("ENCRYPTION=\"Y\""), CreateOptions());
    }
}
