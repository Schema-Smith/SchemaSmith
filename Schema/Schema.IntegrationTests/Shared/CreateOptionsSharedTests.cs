// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// The table options that live only in <c>INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS</c>.
/// <para><b>Why these four ship together.</b> Each surfaces in exactly one place — that single free-text
/// column — and nowhere else in the catalog. They therefore share one reader
/// (<c>SchemaSmith_CreateOption</c>), and splitting them would have meant writing that reader once and
/// wiring it four times across four changes.</para>
/// <para><b>The reader is LOCATE-based rather than regex on purpose.</b> <c>REGEXP_SUBSTR</c> does not
/// exist on MySQL 5.7, the supported floor. A missing function resolves at CALL time rather than at
/// CREATE PROCEDURE time, so a regex version would have kindled cleanly and then failed at the floor on
/// a real deploy — the worse of the two failures, because nothing catches it earlier.</para>
/// <para><b>Three quoting shapes, all observed rather than assumed:</b> MySQL double-quotes the value
/// (<c>COMPRESSION="zlib"</c>), leaves others bare (<c>KEY_BLOCK_SIZE=8</c>), and MariaDB backtick-quotes
/// the <i>key</i> (<c>`PAGE_COMPRESSED`=1</c>).</para>
/// <para>Engine split: <c>Compression</c> is MySQL-only (MariaDB has no such table option and spells the
/// idea <c>PAGE_COMPRESSED</c>); <c>PageCompressed</c> is MariaDB-only; <c>KeyBlockSize</c> is both.</para>
/// </summary>
public abstract class CreateOptionsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection = null!;
    private bool IsMariaDb => Platform == Platform.MariaDb;
    private string TestDb => MainDb;
    private const string TableName = "create_opts_test";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
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

    [SetUp]
    public void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{TestDb}`.`{TableName}`");

    private string CreateOptions() => ScalarStr(
        $"SELECT COALESCE(CREATE_OPTIONS, '') FROM INFORMATION_SCHEMA.TABLES "
        + $"WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = '{TableName}'") ?? "";

    private void Deploy(string extraProps)
    {
        var json = "[{ \"Name\": \"`" + TableName + "`\", \"Engine\": \"InnoDB\"" + extraProps
                   + ", \"Columns\": [ { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false } ],"
                   + " \"Indexes\": [ { \"Name\": \"`pk_" + TableName + "`\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"`id`\" } ] }]";
        Exec($"CALL SchemaSmith_TableQuench('CreateOptsProduct', '{TestDb}', '{json.Replace("'", "''")}', 0, 0, 0)");
    }

    private string ExtractedJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{TestDb}', '{TableName}')";
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? "" : r.ToString();
    }

    [Test]
    public void KeyBlockSize_IsAppliedAndRoundTrips()
    {
        // The one option both engines share. It is the compressed-page size OF ROW_FORMAT=COMPRESSED, so
        // it is declared alongside it rather than alone -- the PadIndex/FillFactor relationship.
        Deploy(", \"RowFormat\": \"COMPRESSED\", \"KeyBlockSize\": 8");

        Assert.Multiple(() =>
        {
            // MySQL reports KEY_BLOCK_SIZE=8, MariaDB key_block_size=8. The reader is case-insensitive;
            // this assertion has to be too, or it pins one engine's spelling as if it were the contract.
            Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain("KEY_BLOCK_SIZE=8"), CreateOptions());
            Assert.That(ExtractedJson(), Does.Contain("KeyBlockSize"), ExtractedJson());
        });
    }

    private bool? _canPunchHoles;

    // MySQL refuses COMPRESSION (error 4029) where the data directory's filesystem cannot punch holes, as on
    // Windows. CI's Linux containers can, so the degrade tests below run only against a server that cannot.
    private bool ServerCanPunchHoles()
    {
        if (_canPunchHoles is { } known) return known;
        const string probe = "create_opts_punch_probe";
        try
        {
            Exec($"CREATE TABLE `{TestDb}`.`{probe}` (id INT PRIMARY KEY) ENGINE=InnoDB COMPRESSION='zlib'");
            _canPunchHoles = true;
        }
        catch (MySqlException ex) when (ex.Number == 4029)
        {
            _canPunchHoles = false;
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{TestDb}`.`{probe}`");
        }
        return _canPunchHoles.Value;
    }

    private void RequireServerWithoutHolePunching()
    {
        if (IsMariaDb)
            Assert.Ignore("MariaDB has no COMPRESSION table option at any version; it spells this PAGE_COMPRESSED.");
        if (ServerCanPunchHoles())
            Assert.Ignore("This server can punch holes, so COMPRESSION applies; run against a Windows data directory to exercise the degrade.");
    }

    private long TableCount() => Convert.ToInt64(ScalarStr(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = '{TableName}'"));

    [Test]
    public void Compression_IsAppliedAndRoundTrips_OnMySqlOnly()
    {
        if (IsMariaDb)
            Assert.Ignore("MariaDB has no COMPRESSION table option at any version; it spells this PAGE_COMPRESSED.");
        if (!ServerCanPunchHoles())
            Assert.Ignore("This server cannot punch holes, so COMPRESSION degrades; the degrade tests cover it.");

        Deploy(", \"Compression\": \"zlib\"");

        Assert.Multiple(() =>
        {
            Assert.That(CreateOptions(), Does.Contain("zlib"), CreateOptions());
            Assert.That(ExtractedJson(), Does.Contain("Compression"), ExtractedJson());
        });
    }

    [Test]
    public void Compression_OnAServerThatCannotPunchHoles_CreatesTheTableWithoutIt_AndRecordsTheDowngrade()
    {
        RequireServerWithoutHolePunching();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName = '{TableName}'");

        Deploy(", \"Compression\": \"zlib\", \"Comment\": \"kept\"");

        Assert.Multiple(() =>
        {
            Assert.That(TableCount(), Is.EqualTo(1), "the table must still be created");
            Assert.That(CreateOptions().ToUpperInvariant(), Does.Not.Contain("COMPRESSION="), CreateOptions());
            Assert.That(ScalarStr($"SELECT TABLE_COMMENT FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = '{TableName}'"),
                Is.EqualTo("kept"), "only the COMPRESSION option may be dropped, not the rest of the table's options");
            Assert.That(ScalarStr("SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType = 'downgraded' "
                                  + $"AND ObjectType = 'table without its COMPRESSION option' AND ObjectName = '{TableName}'"),
                Is.EqualTo("1"), "the lost compression must be recorded as a downgrade");
        });
    }

    [Test]
    public void Compression_OnAServerThatCannotPunchHoles_FailsTheDeployUnderPolicyFail()
    {
        RequireServerWithoutHolePunching();
        Exec("SET @schemasmith_unsupported_policy = 'fail'");
        try
        {
            Assert.That(() => Deploy(", \"Compression\": \"zlib\""),
                Throws.Exception.With.Message.Contains("hole punching"));
            Assert.That(TableCount(), Is.Zero, "under 'fail' the table must not be created without its compression");
        }
        finally
        {
            Exec("SET @schemasmith_unsupported_policy = NULL");
        }
    }

    [Test]
    public void PageCompressed_IsAppliedAndRoundTrips_OnMariaDbOnly()
    {
        if (!IsMariaDb)
            Assert.Ignore("PAGE_COMPRESSED is MariaDB-only; MySQL spells this COMPRESSION.");

        Deploy(", \"PageCompressed\": true, \"PageCompressionLevel\": 6");

        Assert.Multiple(() =>
        {
            Assert.That(CreateOptions().ToUpperInvariant(), Does.Contain("PAGE_COMPRESSED"), CreateOptions());
            Assert.That(ExtractedJson(), Does.Contain("PageCompressed"), ExtractedJson());
        });
    }

    [Test]
    public void ATableDeclaringNone_ExtractsWithoutAnyOfThem()
    {
        // The no-churn contract. Emitting these for every table would rewrite every committed MySQL and
        // MariaDB package -- and would put a MariaDB-only property into MySQL packages, whose schema
        // rejects it.
        Deploy("");

        var json = ExtractedJson();
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("KeyBlockSize"), json);
            Assert.That(json, Does.Not.Contain("Compression"), json);
            Assert.That(json, Does.Not.Contain("PageCompressed"), json);
        });
    }

    [Test]
    public void TheOtherEnginesProperty_IsNeverEmitted()
    {
        // Each option is a hard syntax error on the other engine, so the emit is gated in SQL as well as
        // by the domain's Platforms scoping -- a hand-authored package can still name a property its
        // schema does not declare, and that must not reach the server.
        Assert.DoesNotThrow(() => Deploy(IsMariaDb
            ? ", \"Compression\": \"zlib\""
            : ", \"PageCompressed\": true"));

        Assert.That(CreateOptions().ToUpperInvariant(),
            IsMariaDb ? Does.Not.Contain("COMPRESSION=") : Does.Not.Contain("PAGE_COMPRESSED"),
            CreateOptions());
    }

    [Test]
    public void RedeployingIsIdempotent()
    {
        // Deliberately NOT combined with Compression / PageCompressed: both engines refuse either of
        // those alongside ROW_FORMAT=COMPRESSED (MySQL 1031, MariaDB errno 140), which is what SS-CO-001
        // now reports before a deploy ever sees it.
        const string props = ", \"RowFormat\": \"COMPRESSED\", \"KeyBlockSize\": 8";
        Deploy(props);
        var first = CreateOptions();

        Deploy(props);

        Assert.That(CreateOptions(), Is.EqualTo(first),
            "a second identical deploy must not change the table's options");
    }
}
