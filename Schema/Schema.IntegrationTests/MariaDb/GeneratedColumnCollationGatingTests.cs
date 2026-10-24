// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB before 10.2.37, 10.3.28, 10.4.18 and 10.5.9 rejects a COLLATE clause on a generated column (1064, measured on
/// 10.2.36, 10.3.27, 10.4.17 and 10.5.8), so a package declaring one failed to deploy there. Below those patches the
/// column is created in its character set's default collation and the downgrade recorded. Whether the engine takes the
/// clause is asked of the engine itself, not of SchemaSmith's gate: on a server that rejects it the tests run as they
/// are, and on one that takes it the older patches are simulated with the patch override.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class GeneratedColumnCollationGatingTests
{
    private const string TableName = "ss_gen_collate";
    private IDbConnection _connection = null!;
    private string _testDb = null!;
    private bool _engineTakesCollate;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _engineTakesCollate = EngineTakesGeneratedColumnCollate();
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

    private bool EngineTakesGeneratedColumnCollate()
    {
        try
        {
            Exec("CREATE TEMPORARY TABLE ss_gen_collate_probe (a INT, g VARCHAR(5) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin AS (CONCAT('x', a)) VIRTUAL)");
            Exec("DROP TEMPORARY TABLE ss_gen_collate_probe");
            return true;
        }
        catch (MySqlException e) when (e.Number == 1064)
        {
            return false;
        }
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

    // A server that rejects the clause is already below the fix; a server that takes it is told an older patch.
    private void BelowTheFix(int simulatedPatch)
    {
        if (_engineTakesCollate)
            Exec($"SET @schemasmith_version_patch_override = {simulatedPatch}");
    }

    private static string Json(string characterSet, string collation) => $$"""
        [{ "Name": "`{{TableName}}`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                        { "Name": "`Gen`", "DataType": "VARCHAR(30)", {{(characterSet == null ? "" : $"\"CharacterSet\": \"{characterSet}\", ")}}"Collation": "{{collation}}",
                          "GenerationExpression": "concat('x', `Id`)", "Generated": "VIRTUAL", "Nullable": true } ],
           "Indexes": [ { "Name": "`pk_{{TableName}}`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ] }]
        """;

    private void Deploy(string characterSet = "utf8mb4", string collation = "utf8mb4_bin") =>
        Exec($"CALL SchemaSmith_TableQuench('GenCollateGating', '{_testDb}', '{Json(characterSet, collation).Replace("'", "''")}', 0, 0, 0)");

    private string Live(string column) => Str(
        $"SELECT {column} FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'Gen'");

    private long Audits(string actionType) => Count(
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType = '{actionType}' AND ObjectName LIKE '%{TableName}%Gen%'");

    private long OtherAudits() => Count(
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ActionType != 'downgraded' AND ObjectName LIKE '%{TableName}%'");

    // utf8mb4_bin is never a server's default; ascii_bin also shows the character set was read from the collation, since
    // no server defaults to ascii.
    [TestCase("utf8mb4", "utf8mb4_bin", "utf8mb4")]
    [TestCase(null, "utf8mb4_bin", "utf8mb4")]
    [TestCase(null, "ascii_bin", "ascii")]
    public void BelowTheFix_TheColumnKeepsItsCharacterSetInItsDefault_AndTheSkipIsRecorded_AndARedeployLeavesIt(
        string characterSet, string collation, string expectedSet)
    {
        BelowTheFix(100236);

        Deploy(characterSet, collation);
        var position = Live("ORDINAL_POSITION");
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
        Deploy(characterSet, collation);

        Assert.Multiple(() =>
        {
            Assert.That(Live("CHARACTER_SET_NAME"), Is.EqualTo(expectedSet), "the column keeps the declared character set");
            Assert.That(Live("COLLATION_NAME"), Does.StartWith(expectedSet + "_").And.Not.EqualTo(collation), "in that set's default collation");
            Assert.That(Audits("downgraded"), Is.EqualTo(1), "the skipped COLLATE is recorded on each deploy");
            Assert.That(OtherAudits(), Is.Zero, "a redeploy changes nothing: no drop, add or modify");
            Assert.That(Live("ORDINAL_POSITION"), Is.EqualTo(position), "the column was not re-created");
        });
    }

    [Test]
    public void BelowTheFix_UnderFail_TheDeployIsRefusedNamingTheVersions_BeforeAnythingIsCreated()
    {
        BelowTheFix(100236);
        Exec("SET @schemasmith_unsupported_policy = 'fail'");

        Assert.That(() => Deploy(), Throws.Exception.With.Message.Contains("10.2.37"));
        Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'"),
            Is.Zero, "nothing is created");
    }

    // A variant the target does not take is gone before anything is reported: it must not be recorded, or refused.
    [Test]
    public void BelowTheFix_AVariantThatDoesNotApply_IsNeitherRecordedNorRefused()
    {
        BelowTheFix(100236);
        Exec("SET @schemasmith_unsupported_policy = 'fail'");
        var json = Json("utf8mb4", "utf8mb4_bin").Replace("\"Nullable\": true } ]", "\"Nullable\": true, \"ShouldApplyExpression\": \"SELECT 0\" } ]");
        Assert.That(json, Does.Contain("SELECT 0"), "precondition: the generated column is a variant that does not apply");

        Assert.DoesNotThrow(() => Exec($"CALL SchemaSmith_TableQuench('GenCollateGating', '{_testDb}', '{json.Replace("'", "''")}', 0, 0, 0)"));
        Assert.That(Audits("downgraded"), Is.Zero);
    }

    [TestCase(100237)]
    [TestCase(100328)]
    [TestCase(100418)]
    [TestCase(100509)]
    [TestCase(100600)]
    public void AtEachFixedPatch_TheDeclaredCollationIsApplied(int patch)
    {
        if (!_engineTakesCollate)
            Assert.Ignore("This server rejects COLLATE on a generated column, so it cannot be told it takes one.");
        Exec($"SET @schemasmith_version_patch_override = {patch}");

        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(Live("COLLATION_NAME"), Is.EqualTo("utf8mb4_bin"));
            Assert.That(Audits("downgraded"), Is.Zero);
        });
    }

    [TestCase(100327)]
    [TestCase(100417)]
    [TestCase(100508)]
    public void JustBeforeEachLinesFix_TheCollationIsSkipped(int patch)
    {
        BelowTheFix(patch);

        Deploy();

        Assert.That(Audits("downgraded"), Is.EqualTo(1));
    }
}
