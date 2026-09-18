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
/// CHECK-constraint version-gating for the MySQL floor lowering (MySQL 5.7).
/// INFORMATION_SCHEMA.CHECK_CONSTRAINTS + CHECK enforcement arrived at MySQL 8.0.16; on MySQL 5.7 a
/// declared CHECK is parsed-and-ignored, so the emit must degrade through SchemaSmith_UnsupportedFeaturePolicy:
/// 'warn' (default) skips the emit + records a 'downgraded' manifest row (idempotent); 'fail' aborts.
///
/// These bodies run on the modern 8.0 CI container and drive the degrade LOGIC via
/// @schemasmith_version_override = 507 (which forces SchemaSmith_SupportsCheckConstraints() -> 0 on a
/// non-MariaDB server), mirroring the PostgreSQL version-override gating tests. The genuine MySQL 5.7 /
/// MariaDB 10.2 end-to-end behavior is validated out-of-band against the throwaway floor containers.
/// MariaDB is intentionally not exercised here: it reports CHECK support regardless of the version
/// override (VERSION() LIKE '%MariaDB%'), so the degrade path is MySQL-only by construction.
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class CheckConstraintGatingTests
{
    private const string TableName = "chk_gate_test";
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

    private static string BuildTableJson()
    {
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`qty`", DataType = "INT", Nullable = true }
            ],
            Indexes =
            [
                new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ],
            CheckConstraints =
            [
                new CheckConstraint { Name = $"`CK_{TableName}_pos`", Expression = "`qty` < 1000" },
                // Two constraints, both table-level: the column-level CheckExpression alias is retired, and the
                // pair is what makes the count below prove the whole set is gated together rather than one of it.
                new CheckConstraint { Name = $"`CK_{TableName}_nonneg`", Expression = "`qty` >= 0" }
            ]
        };
        return "[" + JsonConvert.SerializeObject(table) + "]";
    }

    private void Deploy()
    {
        var json = BuildTableJson().Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('CheckGatingProduct', '{_testDb}', '{json}', 0, 0, 0)");
    }

    private long LiveCheckCount() => Scalar(
        $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND CONSTRAINT_TYPE = 'CHECK'");

    private long TableExistsCount() => Scalar(
        $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'");

    // Scope to CHECK-constraint audit rows (names are CK_<table>_*), excluding the PK-index/table rows.
    private long CheckAuditCount(string actionType) => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = '{actionType}' AND ObjectName LIKE '%CK_{TableName}%'");

    // ---- degrade path (MySQL 5.7 simulated via version override) -----------

    [Test]
    public void BelowFloor_Warn_DeploysTableSkipsCheckAndRecordsDowngraded()
    {
        SetVersionOverride(507);
        SetPolicy("warn");

        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(TableExistsCount(), Is.EqualTo(1), "Table must still deploy on MySQL 5.7.");
            Assert.That(LiveCheckCount(), Is.EqualTo(0), "CHECK constraints must be skipped below MySQL 8.0.16.");
            Assert.That(CheckAuditCount("downgraded"), Is.GreaterThanOrEqualTo(2),
                "Each declared check (table-level + column-level) must record a 'downgraded' manifest row.");
            Assert.That(CheckAuditCount("created"), Is.EqualTo(0),
                "No check-constraint 'created' audit row may be recorded below the floor.");
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
            Assert.That(LiveCheckCount(), Is.EqualTo(0), "The check must remain absent after a second deploy.");
            Assert.That(CheckAuditCount("created"), Is.EqualTo(0),
                "A second deploy must not falsely record a check-constraint 'created' row (non-idempotency bug).");
        });
    }

    [Test]
    public void BelowFloor_Fail_AbortsNamingTheOffendingCheck()
    {
        SetVersionOverride(507);
        SetPolicy("fail");

        Assert.That(Deploy, Throws.Exception.With.Message.Contains("8.0.16"),
            "Under policy 'fail' a declared check below the floor must abort the deploy.");
    }

    // ---- supported path (modern binary, no override) -----------------------

    [Test]
    public void ModernServer_CreatesAndEnforcesCheck()
    {
        // Enforcement is only assertable where the engine actually supports CHECK constraints (MySQL 8.0.16+
        // or MariaDB). On a genuine below-floor target (MySQL 5.7) checks degrade, so this positive test skips.
        if (Scalar("SELECT SchemaSmith_SupportsCheckConstraints()") == 0)
            Assert.Ignore("Target does not enforce CHECK constraints (MySQL < 8.0.16); covered by the BelowFloor_* tests.");

        // No version override: on a real 8.0 server SupportsCheckConstraints() = 1, so the check is created.
        SetPolicy("warn");

        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(TableExistsCount(), Is.EqualTo(1), "Table must deploy on the modern binary.");
            Assert.That(LiveCheckCount(), Is.EqualTo(2), "Both declared checks must be created.");
            Assert.That(CheckAuditCount("downgraded"), Is.EqualTo(0), "No downgrade may be recorded on a supported server.");
        });
    }
}
