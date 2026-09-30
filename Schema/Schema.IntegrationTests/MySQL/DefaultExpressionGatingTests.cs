// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MySQL;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// Column DEFAULT-expression version-gating for the MySQL floor lowering (MySQL 5.7). Expression defaults
/// (e.g. DEFAULT (CURRENT_DATE + INTERVAL 1 YEAR)) require MySQL 8.0.13; below that a declared expression
/// default is a hard syntax error at the engine, so the emit must degrade through
/// SchemaSmith_UnsupportedFeaturePolicy: 'warn' (default) skips the column + records a 'downgraded'
/// manifest row (idempotent); 'fail' aborts.
///
/// These bodies run on the modern 8.0 CI container and drive the degrade LOGIC via
/// @schemasmith_version_override = 507 (which forces SchemaSmith_SupportsDefaultExpression() -> 0 on a
/// non-MariaDB server), mirroring CheckConstraintGatingTests. The genuine MySQL 5.7 end-to-end behavior is
/// validated out-of-band against the throwaway floor container. MariaDB is intentionally not exercised
/// here: MDEV-10134 landed in 10.2.1 (the first point release of the 10.2 series, at/below our floor), so
/// SchemaSmith_SupportsDefaultExpression() reports 1 for MariaDB regardless of the override
/// (VERSION() LIKE '%MariaDB%' short-circuits first) — see Schema.IntegrationTests.MariaDb.DefaultExpressionGatingTests.
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class DefaultExpressionGatingTests
{
    private const string TableName = "def_expr_gate_test";
    private const string ExpressionDefault = "(CURRENT_DATE + INTERVAL 1 YEAR)";
    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
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
        // ChangeAudit persists across the fixture's shared connection; scope each test to its own rows.
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

    private void SetVersionOverride(int? major) =>
        Exec(major.HasValue ? $"SET @schemasmith_version_override = {major.Value}" : "SET @schemasmith_version_override = NULL");

    private void SetPolicy(string policy) =>
        Exec(policy == null ? "SET @schemasmith_unsupported_policy = NULL" : $"SET @schemasmith_unsupported_policy = '{policy}'");

    private void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    private static string BuildTableJson(string defaultValue)
    {
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`expiry_date`", DataType = "DATE", Nullable = false, Default = defaultValue }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        return "[" + JsonConvert.SerializeObject(table) + "]";
    }

    private void Deploy() => Deploy(ExpressionDefault);

    private void Deploy(string defaultValue)
    {
        var json = BuildTableJson(defaultValue).Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('DefExprGateProduct', '{_testDb}', '{json}', 0, 0, 0)");
    }

    private long TableExistsCount() => Scalar(
        $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'");

    private long ColumnExistsCount() => Scalar(
        $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'expiry_date'");

    private long DowngradedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'downgraded' AND ObjectName LIKE '%{TableName}.expiry_date%'");

    private long CreatedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'created' AND ObjectName LIKE '%{TableName}.expiry_date%'");

    // ---- degrade path (MySQL 5.7 simulated via version override) -----------

    // A bare function default is emitted in the expression form too (wrapped in parentheses), so it has to be
    // degraded the same way -- the gate once recognised only the parenthesised spelling, and a hand-written
    // curdate() reached MySQL 5.7 as DEFAULT (curdate()), a syntax error.
    [TestCase(ExpressionDefault)]
    [TestCase("curdate()")]
    public void BelowFloor_Warn_DeploysTableSkipsColumnAndRecordsDowngraded(string defaultValue)
    {
        SetVersionOverride(507);
        SetPolicy("warn");

        Deploy(defaultValue);

        Assert.Multiple(() =>
        {
            Assert.That(TableExistsCount(), Is.EqualTo(1), "Table must still deploy on MySQL 5.7.");
            Assert.That(ColumnExistsCount(), Is.EqualTo(0), "The expression-default column must be skipped below MySQL 8.0.13.");
            Assert.That(DowngradedAuditCount(), Is.EqualTo(1), "The skipped column must record a 'downgraded' manifest row.");
            Assert.That(CreatedAuditCount(), Is.EqualTo(0), "No 'created' audit row may be recorded for a column skipped below the floor.");
        });
    }

    [Test]
    public void BelowFloor_Warn_SecondDeployIsIdempotent()
    {
        SetVersionOverride(507);
        SetPolicy("warn");

        Deploy();
        Assert.DoesNotThrow(Deploy, "A second deploy below the floor must not error.");

        Assert.Multiple(() =>
        {
            Assert.That(ColumnExistsCount(), Is.EqualTo(0), "The column must remain absent after a second deploy.");
            Assert.That(CreatedAuditCount(), Is.EqualTo(0),
                "A second deploy must not falsely record a 'created' row (non-idempotency bug).");
        });
    }

    [TestCase(ExpressionDefault)]
    [TestCase("curdate()")]
    public void BelowFloor_Fail_AbortsNamingTheOffendingColumn(string defaultValue)
    {
        SetVersionOverride(507);
        SetPolicy("fail");

        Assert.That(() => Deploy(defaultValue), Throws.Exception.With.Message.Contains("8.0.13"),
            "Under policy 'fail' a declared expression default below the floor must abort the deploy.");
    }

    // A quoted literal is a plain default on every version, parenthesis or not. Wrapped like a function it
    // became DEFAULT ('...'), which MySQL 5.7 rejects.
    [Test]
    public void BelowFloor_AQuotedLiteralContainingAParenthesis_DeploysAsALiteral()
    {
        SetVersionOverride(507);
        SetPolicy("fail");

        Assert.That(() => DeployVarchar("'N/A (none)'"), Throws.Nothing);
        Assert.That(Scalar($@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                              WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'
                                AND COLUMN_NAME = 'note' AND COLUMN_DEFAULT = 'N/A (none)'"), Is.EqualTo(1));
    }

    private void DeployVarchar(string defaultValue)
    {
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`note`", DataType = "VARCHAR(40)", Nullable = true, Default = defaultValue }
            ],
            Indexes = [ new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" } ]
        };
        var json = ("[" + JsonConvert.SerializeObject(table) + "]").Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('DefExprGateProduct', '{_testDb}', '{json}', 0, 0, 0)");
    }

    // ---- supported path (modern binary, no override) -----------------------

    [Test]
    public void ModernServer_CreatesColumnWithExpressionDefault()
    {
        // The positive path is only assertable where the engine actually supports expression defaults
        // (MySQL 8.0.13+ or MariaDB). On a genuine below-floor target (MySQL 5.7) the column degrades,
        // so this positive test skips there.
        if (Scalar("SELECT SchemaSmith_SupportsDefaultExpression()") == 0)
            Assert.Ignore("Target does not support expression defaults (MySQL < 8.0.13); covered by the BelowFloor_* tests.");

        // No version override: on a real 8.0.13+ server SupportsDefaultExpression() = 1, so the column is created.
        SetPolicy("warn");

        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(TableExistsCount(), Is.EqualTo(1), "Table must deploy on the modern binary.");
            Assert.That(ColumnExistsCount(), Is.EqualTo(1), "The expression-default column must be created.");
            Assert.That(DowngradedAuditCount(), Is.EqualTo(0), "No downgrade may be recorded on a supported server.");
        });
    }
}
