// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// At-rest table encryption (F2a) on MariaDB: <c>ENCRYPTED=YES</c> (+ optional
/// <c>ENCRYPTION_KEY_ID</c>), applied on CREATE (<c>SchemaSmith_MissingTableAndColumnQuench</c>) and
/// converged on an existing table (<c>SchemaSmith_ModifiedTableQuench</c> STEP 6.5).
/// <para><b>Why this is a separate file from <c>CreateOptionsSharedTests</c>, not an addition to it.</b>
/// That shared fixture runs Compression/PageCompressed/KeyBlockSize on the ordinary demo containers,
/// which have no key-management backend. Encryption needs one -- <c>scripts/run-encryption-sweep.sh</c>
/// builds a purpose-built MariaDB image with the <c>file_key_management</c> plugin and runs only the
/// <c>Encryption</c>+<c>MariaDb</c> category against it, the same pattern the genuine-binary sweep uses
/// for pre-2016 SQL Server. The normal gate/CI does NOT run these tests.</para>
/// <para><b>Availability gate.</b> <see cref="OneTimeSetUp"/> queries
/// <c>information_schema.PLUGINS</c> for <c>file_key_management</c> and <c>Assert.Ignore</c>s the whole
/// fixture when it is absent, so this file is also safe to run (as a no-op) against an ordinary MariaDB
/// container that never opted into the encryption image.</para>
/// <para><b>MySQL has no counterpart here.</b> MySQL's <c>component_keyring_file</c> will not initialize
/// inside the stock Oracle <c>mysql:8.0</c> entrypoint (bug #108197 family) -- see
/// <c>Schema.IntegrationTests.MySQL.EncryptionQuenchTests</c>, a single <c>[Explicit]</c> test documenting
/// that gap, and <c>scripts/test-infra/encryption/README.md</c>.</para>
/// </summary>
[Category("MariaDb")]
[Category("Encryption")]
[TestFixture]
public class EncryptionQuenchTests
{
    private const string TableName = "encryption_quench_test";
    // Fixed TEST key from scripts/test-infra/encryption/mariadb/keyfile.txt -- not a secret, a fixture.
    private const int TestKeyId = 1;

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        var pluginStatus = ScalarStr(
            "SELECT PLUGIN_STATUS FROM information_schema.PLUGINS WHERE PLUGIN_NAME = 'file_key_management'");
        if (!string.Equals(pluginStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            Assert.Ignore("file_key_management plugin is not ACTIVE on this server -- encryption is unavailable. "
                           + "Run scripts/run-encryption-sweep.sh, which provisions it. PLUGIN_STATUS was: "
                           + (pluginStatus ?? "<absent>"));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    [TearDown]
    public void TearDown() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

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

    // MariaDB backtick-wraps the option KEY in CREATE_OPTIONS (e.g. `ENCRYPTED`=YES), unlike MySQL which
    // leaves keys bare (e.g. COMPRESSION="zlib"). Strip backticks here the same way
    // SchemaSmith_CreateOption itself does (REPLACE(p_Options, '`', '')) so every substring assertion in
    // this file that inspects CREATE_OPTIONS text is backtick-robust -- without this, Does.Contain
    // ("ENCRYPTED=YES") pins MySQL's bare-key shape and fails on MariaDB even when the table genuinely
    // is encrypted, which is exactly the raw-string pinning Rule 32 warns against.
    private string CreateOptions() => (ScalarStr(
        $"SELECT COALESCE(CREATE_OPTIONS, '') FROM INFORMATION_SCHEMA.TABLES "
        + $"WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'") ?? "").Replace("`", "");

    // Mirrors CreateOptionsSharedTests.Deploy: a single primary-keyed table, extraProps spliced into the
    // table object so each test declares only what it is testing.
    private void Deploy(string extraProps)
    {
        var json = "[{ \"Name\": \"`" + TableName + "`\", \"Engine\": \"InnoDB\"" + extraProps
                   + ", \"Columns\": [ { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false } ],"
                   + " \"Indexes\": [ { \"Name\": \"`pk_" + TableName + "`\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"`id`\" } ] }]";
        Exec($"CALL SchemaSmith_TableQuench('EncryptionQuenchProduct', '{_testDb}', '{json.Replace("'", "''")}', 0, 0, 0)");
    }

    private string ExtractedJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')";
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? "" : r.ToString();
    }

    // ---- (a) applied on CREATE, round-trips through extraction --------------------------------------

    [Test]
    public void Encrypted_IsAppliedAndRoundTrips()
    {
        Deploy(", \"Encrypted\": true");

        Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain("ENCRYPTED=YES"), CreateOptions());

        // Deserialize rather than substring-match the raw JSON: what matters is the domain property's
        // VALUE round-trips true, not that some literal text appears in the extracted blob (Rule 32).
        var extracted = ExtractedJson();
        if (PlatformDeserializer.DeserializeTable(extracted, Platform.MariaDb) is not MariaDbTable table)
        {
            Assert.Fail("Extraction must deserialize to a MariaDbTable.");
            return;
        }
        Assert.That(table.Encrypted, Is.True, extracted);
    }

    [Test]
    public void EncryptionKeyId_RoundTrips()
    {
        Deploy($", \"Encrypted\": true, \"EncryptionKeyId\": {TestKeyId}");

        Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain($"ENCRYPTION_KEY_ID={TestKeyId}"), CreateOptions());

        var extracted = ExtractedJson();
        if (PlatformDeserializer.DeserializeTable(extracted, Platform.MariaDb) is not MariaDbTable table)
        {
            Assert.Fail("Extraction must deserialize to a MariaDbTable.");
            return;
        }
        Assert.Multiple(() =>
        {
            Assert.That(table.Encrypted, Is.True, extracted);
            Assert.That(table.EncryptionKeyId, Is.EqualTo(TestKeyId), extracted);
        });
    }

    [Test]
    public void ATableDeclaringNoEncryption_ExtractsWithoutIt()
    {
        // The no-churn contract (mirrors CreateOptionsSharedTests.ATableDeclaringNone_ExtractsWithoutAnyOfThem):
        // an ordinary table's package must not carry Encrypted/EncryptionKeyId at all.
        Deploy("");

        var json = ExtractedJson();
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("Encrypted"), json);
            Assert.That(json, Does.Not.Contain("EncryptionKeyId"), json);
            Assert.That(CreateOptions().ToUpperInvariant(), Does.Not.Contain("ENCRYPTED"), CreateOptions());
        });
    }

    [Test]
    public void MySqlOnlyEncryptionProperty_IsNeverEmittedOnMariaDb()
    {
        // MySQL's ENCRYPTION='Y' is a hard syntax error on MariaDB. A hand-authored package can still
        // name it (the raw JSON parser is engine-agnostic -- see ParseTableJson), so the emit-side gate
        // in MissingTableAndColumnQuench/ModifiedTableQuench must never let it reach the server, mirroring
        // CreateOptionsSharedTests.TheOtherEnginesProperty_IsNeverEmitted for Compression/PageCompressed.
        Assert.DoesNotThrow(() => Deploy(", \"Encryption\": \"Y\""));

        Assert.That(CreateOptions().ToUpperInvariant(), Does.Not.Contain("ENCRYPTION="), CreateOptions());
    }

    // ---- (b) converge: an EXISTING table becomes encrypted (ModifiedTableQuench STEP 6.5) -----------

    [Test]
    public void Converge_PlainTableRedeployedEncrypted_BecomesEncrypted()
    {
        Deploy("");
        Assert.That(CreateOptions().ToUpperInvariant(), Does.Not.Contain("ENCRYPTED"),
            "Precondition: table must deploy unencrypted first.");

        Deploy(", \"Encrypted\": true");

        Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain("ENCRYPTED=YES"),
            "Redeploying an existing table with Encrypted:true must ALTER it to encrypted (STEP 6.5).");
    }

    [Test]
    public void Converge_EncryptedTableRedeployedPlain_BecomesUnencrypted()
    {
        // The reverse direction. Unlike system versioning's DROP guard, disabling table encryption is a
        // plain reversible ALTER (no row history to purge), so STEP 6.5 converges this direction too.
        Deploy(", \"Encrypted\": true");
        Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain("ENCRYPTED=YES"),
            "Precondition: table must deploy encrypted first.");

        // Encrypted is a non-nullable bool (like PageCompressed): omitting the key from the JSON
        // deserializes to false, which is what "declared unencrypted" looks like at this layer.
        Deploy("");

        Assert.That(CreateOptions().ToUpperInvariant(), Does.Not.Contain("ENCRYPTED=YES"),
            "Redeploying the same table without Encrypted must ALTER it back to unencrypted (STEP 6.5).");
    }

    // ---- (c) idempotent redeploy --------------------------------------------------------------------

    [Test]
    public void RedeployingEncrypted_IsIdempotent()
    {
        const string props = ", \"Encrypted\": true";
        Deploy(props);
        var first = CreateOptions();

        Assert.DoesNotThrow(() => Deploy(props),
            "A second identical deploy of an already-encrypted table must not error.");

        Assert.That(CreateOptions(), Is.EqualTo(first),
            "A second identical deploy must not change the table's options.");
    }
}
