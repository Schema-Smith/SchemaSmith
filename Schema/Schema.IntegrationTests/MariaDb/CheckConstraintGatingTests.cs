// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB enforces CHECK constraints from 10.2.1, but INFORMATION_SCHEMA.CHECK_CONSTRAINTS, which every read of a
/// deployed check needs, arrived in 10.2.22. Below it a declared CHECK degrades through the unsupported-feature policy,
/// as it does on MySQL before 8.0.16. 10.2.21 and 10.2.22 are simulated with the patch override on the modern container;
/// the floor sweep's MariaDB 10.2 leg is 10.2.44.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class CheckConstraintGatingTests
{
    private const string TableName = "chk_gate_maria";
    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
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
        Reset();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
    }

    [TearDown]
    public void TearDown() => Reset();

    private void Reset()
    {
        Exec("SET @schemasmith_version_patch_override = NULL");
        Exec("SET @schemasmith_unsupported_policy = NULL");
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
    }

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

    private void Deploy() => DeployTable(columnCheck: false);

    private void DeployColumnCheck() => DeployTable(columnCheck: true);

    private void DeployTable(bool columnCheck)
    {
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false },
                new MariaDbColumn { Name = "`qty`", DataType = "INT", Nullable = true, CheckExpression = columnCheck ? "`qty` >= 0" : null }
            ],
            Indexes = [new Schema.Domain.Index { Name = $"`pk_{TableName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }],
            CheckConstraints = columnCheck ? [] : [new CheckConstraint { Name = $"`CK_{TableName}_pos`", Expression = "`qty` >= 0" }]
        };
        var json = ("[" + JsonConvert.SerializeObject(table) + "]").Replace("'", "''");
        Exec($"CALL SchemaSmith_TableQuench('CheckGatingMaria', '{_testDb}', '{json}', 0, 0, 0)");
    }

    private long LiveCheckCount() => Scalar(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND CONSTRAINT_TYPE = 'CHECK'");

    private long CheckAuditCount(string actionType) => Scalar(
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType = '{actionType}' AND ObjectName LIKE '%CK_{TableName}%'");

    [Test]
    public void Below10222_Warn_DeploysTheTableWithoutTheCheck_AndRedeploysQuietly()
    {
        Exec("SET @schemasmith_version_patch_override = 100221");

        Deploy();
        Assert.DoesNotThrow(Deploy, "a redeploy below 10.2.22 must not read the CHECK_CONSTRAINTS view");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'"),
                Is.EqualTo(1), "the table must deploy");
            Assert.That(LiveCheckCount(), Is.Zero, "the check must be skipped below 10.2.22");
            Assert.That(CheckAuditCount("downgraded"), Is.GreaterThanOrEqualTo(1), "the skip must be recorded as a downgrade");
            Assert.That(CheckAuditCount("created"), Is.Zero);
        });
    }

    private long ColumnAuditCount(string actionType) => Scalar(
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType = '{actionType}' AND ObjectName LIKE '%{TableName}%qty%'");

    // A column's own check rides its definition. Below 10.2.22 it cannot be read back, so it is skipped as a table's check
    // is; carried in the definition, it re-applied the column on every deploy against a live check it could not see.
    [Test]
    public void Below10222_Warn_AColumnCheck_IsSkippedAndRecorded_AndARedeployLeavesTheColumn()
    {
        Exec("SET @schemasmith_version_patch_override = 100221");

        DeployColumnCheck();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
        DeployColumnCheck();

        Assert.Multiple(() =>
        {
            Assert.That(LiveCheckCount(), Is.Zero, "the column's check must be skipped below 10.2.22");
            Assert.That(ColumnAuditCount("downgraded"), Is.EqualTo(1), "the skip must be recorded as a downgrade on each deploy");
            Assert.That(ColumnAuditCount("modified"), Is.Zero, "a redeploy must leave the column alone");
        });
    }

    [Test]
    public void Below10222_Fail_AColumnCheck_AbortsNamingTheMariaDbVersion()
    {
        Exec("SET @schemasmith_version_patch_override = 100221");
        Exec("SET @schemasmith_unsupported_policy = 'fail'");

        Assert.That(DeployColumnCheck, Throws.Exception.With.Message.Contains("MariaDB 10.2.22"));
    }

    [Test]
    public void Below10222_Fail_AbortsNamingTheMariaDbVersion()
    {
        Exec("SET @schemasmith_version_patch_override = 100221");
        Exec("SET @schemasmith_unsupported_policy = 'fail'");

        Assert.That(Deploy, Throws.Exception.With.Message.Contains("MariaDB 10.2.22"));
    }

    [Test]
    public void At10222_TheCheckIsCreated()
    {
        Exec("SET @schemasmith_version_patch_override = 100222");

        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(LiveCheckCount(), Is.EqualTo(1));
            Assert.That(CheckAuditCount("downgraded"), Is.Zero);
        });
    }
}
