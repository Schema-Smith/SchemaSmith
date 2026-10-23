// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB before 10.2.37, 10.3.28, 10.4.18 and 10.5.9 rejects a COLLATE clause on a generated column (1064, measured on
/// 10.2.36, 10.3.27, 10.4.17 and 10.5.8), so a package declaring one failed to deploy there. Below those patches the
/// column is created in its character set's default collation and the downgrade recorded. The older patches are simulated
/// with the patch override on the modern container, which accepts the clause either way.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class GeneratedColumnCollationGatingTests
{
    private const string TableName = "ss_gen_collate";
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

    private string Str(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : Convert.ToString(result);
    }

    private long Count(string sql) => Convert.ToInt64(Str(sql));

    // The override steers SchemaSmith's gates on a newer server. Told a version above its own, a real server would be
    // asked for catalog it lacks (below MariaDB 10.2.22, CHECK_CONSTRAINTS), so such a simulation is skipped there.
    private void Simulate(int patch)
    {
        if (Count("SELECT SchemaSmith_ServerVersionPatchNum()") < patch)
            Assert.Ignore($"This server is older than the simulated {patch}.");
        Exec($"SET @schemasmith_version_patch_override = {patch}");
    }

    private static string Json(string characterSet) => $$"""
        [{ "Name": "`{{TableName}}`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                        { "Name": "`Gen`", "DataType": "VARCHAR(30)", {{(characterSet == null ? "" : $"\"CharacterSet\": \"{characterSet}\", ")}}"Collation": "utf8mb4_bin",
                          "GenerationExpression": "concat('x', `Id`)", "Generated": "VIRTUAL", "Nullable": true } ],
           "Indexes": [ { "Name": "`pk_{{TableName}}`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ] }]
        """;

    private void Deploy(string characterSet = "utf8mb4") =>
        Exec($"CALL SchemaSmith_TableQuench('GenCollateGating', '{_testDb}', '{Json(characterSet).Replace("'", "''")}', 0, 0, 0)");

    private string LiveCollation() => Str(
        $"SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'Gen'");

    private long Audits(string actionType) => Count(
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType = '{actionType}' AND ObjectName LIKE '%{TableName}%Gen%'");

    [TestCase("utf8mb4")]
    [TestCase(null)]
    public void BeforeTheFix_TheColumnTakesItsCharacterSetsDefault_AndTheSkipIsRecorded_AndARedeployLeavesIt(string characterSet)
    {
        Simulate(100236);

        Deploy(characterSet);
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
        Deploy(characterSet);

        Assert.Multiple(() =>
        {
            Assert.That(LiveCollation(), Does.StartWith("utf8mb4_").And.Not.EqualTo("utf8mb4_bin"),
                "the column keeps the declared character set, in its default collation");
            Assert.That(Audits("downgraded"), Is.EqualTo(1), "the skipped COLLATE is recorded on each deploy");
            Assert.That(Audits("modified"), Is.Zero, "a redeploy leaves the column alone");
        });
    }

    [Test]
    public void BeforeTheFix_UnderFail_TheDeployIsRefusedNamingTheVersions()
    {
        Simulate(100236);
        Exec("SET @schemasmith_unsupported_policy = 'fail'");

        Assert.That(() => Deploy(), Throws.Exception.With.Message.Contains("10.2.37"));
        Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'"),
            Is.Zero, "nothing is created");
    }

    [TestCase(100237)]
    [TestCase(100328)]
    [TestCase(100418)]
    [TestCase(100509)]
    [TestCase(100600)]
    public void AtEachFixedPatch_TheDeclaredCollationIsApplied(int patch)
    {
        // The override steers SchemaSmith's gate, not the parser: only a server that takes the clause can be told it does.
        if (Count("SELECT SchemaSmith_SupportsGeneratedColumnCollation()") == 0)
            Assert.Ignore("This server rejects COLLATE on a generated column.");
        Simulate(patch);

        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(LiveCollation(), Is.EqualTo("utf8mb4_bin"));
            Assert.That(Audits("downgraded"), Is.Zero);
        });
    }

    [TestCase(100327)]
    [TestCase(100417)]
    [TestCase(100508)]
    public void JustBeforeEachLinesFix_TheCollationIsSkipped(int patch)
    {
        Simulate(patch);

        Deploy();

        Assert.That(Audits("downgraded"), Is.EqualTo(1));
    }
}
