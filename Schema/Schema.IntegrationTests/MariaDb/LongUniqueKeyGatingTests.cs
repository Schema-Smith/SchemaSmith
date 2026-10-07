// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// A long unique key -- UNIQUE over a column too wide for a B-tree key, which MariaDB 10.4+ enforces through a hidden
/// hash and extracts as IndexType HASH -- cannot be deployed to MariaDB 10.2 or 10.3: InnoDB there turns USING HASH into
/// a B-tree and refuses the key length (1071 / 1170, MA-014). There is no safe degrade, because dropping the key drops
/// the uniqueness, so the deploy is refused by name under either policy, before anything is created. MEMORY tables have
/// real hash indexes on every version and are not affected. 10.2/10.3 are simulated with the version override on the
/// modern container; the floor sweep's MariaDB 10.2 leg runs the same tests against the real engine.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class LongUniqueKeyGatingTests
{
    private const string TableName = "long_unique_gate_test";
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
    public void SetUp() => Reset();

    [TearDown]
    public void TearDown() => Reset();

    private void Reset()
    {
        Exec("SET @schemasmith_version_override = NULL");
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
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string TableJson(string engine, string dataType, bool withLongUniqueKey = true) =>
        $$"""
        [{ "Name": "`{{TableName}}`", "Engine": "{{engine}}",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false },
                        { "Name": "`body`", "DataType": "{{dataType}}", "Nullable": true } ],
           "Indexes": [ { "Name": "`pk_{{TableName}}`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" }
                        {{(withLongUniqueKey ? """, { "Name": "`ux_body`", "Unique": true, "IndexType": "HASH", "IndexColumns": "`body`" }""" : "")}} ] }]
        """;

    private void Deploy(string engine = "InnoDB", string dataType = "TEXT") =>
        Exec($"CALL SchemaSmith_TableQuench('LongUniqueGateProduct', '{_testDb}', '{TableJson(engine, dataType).Replace("'", "''")}', 0, 0, 0)");

    private long TableCount() =>
        Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'");

    [TestCase(null)]
    [TestCase("fail")]
    public void Below104_ALongUniqueKey_IsRefusedByName_BeforeAnythingIsCreated(string policy)
    {
        Exec("SET @schemasmith_version_override = 1003");
        if (policy != null) Exec($"SET @schemasmith_unsupported_policy = '{policy}'");

        var ex = Assert.Catch(() => Deploy());

        Assert.That(ex!.Message, Does.Contain("MariaDB 10.4").And.Contain("ux_body"));
        Assert.That(TableCount(), Is.Zero, "a refused deploy must not create the table");
    }

    [Test]
    public void Below104_AUniqueHashOnAMemoryTable_IsCreated()
    {
        Exec("SET @schemasmith_version_override = 1003");

        Deploy("MEMORY", "VARCHAR(20)");

        Assert.That(TableCount(), Is.EqualTo(1));
    }

    // The --IndexOnly path parses and creates indexes through its own procedure, which calls the same refusal first.
    [Test]
    public void Below104_ALongUniqueKey_OnTheIndexOnlyPath_IsRefused()
    {
        var withoutKey = TableJson("InnoDB", "TEXT", withLongUniqueKey: false);
        Exec($"CALL SchemaSmith_TableQuench('LongUniqueGateProduct', '{_testDb}', '{withoutKey.Replace("'", "''")}', 0, 0, 0)");
        Exec("SET @schemasmith_version_override = 1003");

        Exec($"CALL SchemaSmith_ParseTableJson('{_testDb}', '{TableJson("InnoDB", "TEXT").Replace("'", "''")}')");
        var ex = Assert.Catch(() => Exec($"CALL SchemaSmith_IndexOnlyQuench('LongUniqueGateProduct', '{_testDb}', 0, 0, 0)"));

        Assert.That(ex!.Message, Does.Contain("MariaDB 10.4"));
        Assert.That(Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND INDEX_NAME = 'ux_body'"),
            Is.Zero);
    }

    [Test]
    public void At104_ALongUniqueKey_IsCreated()
    {
        Exec("SET @schemasmith_version_override = 1004");

        Deploy();

        Assert.That(Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND INDEX_NAME = 'ux_body'"),
            Is.EqualTo(1));
    }
}
