// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB names CHECK constraints per TABLE on every version, and from 12.1 foreign keys too (unnamed ones are all
/// "1"). Matching constraints by schema and name alone crosses tables: extraction wrote another table's check onto this
/// one, a redeploy dropped and re-created a check that had not changed, and two same-named foreign keys were merged
/// (MA-009, MA-c1, MA-c14). Every match now keys on the table too. The CHECK cases run on every version; the foreign-key
/// cases need 12.1, so they run on the 12.3 and 13 legs.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class PerTableConstraintNameTests
{
    private const string Product = "PerTableConstraintNameProduct";
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
    public void SetUp() => DropTables();

    [TearDown]
    public void TearDown() => DropTables();

    private void DropTables()
    {
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`ss_ptc_a`, `{_testDb}`.`ss_ptc_b`, `{_testDb}`.`ss_ptc_p`");
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%ss_ptc_%'");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void Deploy(string tablesJson) =>
        Exec($"CALL SchemaSmith_TableQuench('{Product}', '{_testDb}', '{tablesJson.Replace("'", "''")}', 0, 0, 0)");

    private const string TwoTablesWithTheSameCheckName = """
        [{ "Name": "`ss_ptc_a`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`qty`", "DataType": "INT", "Nullable": false } ],
           "Indexes": [ { "Name": "`pk_ss_ptc_a`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ],
           "CheckConstraints": [ { "Name": "`ck_x`", "Expression": "`qty` > 0" } ] },
         { "Name": "`ss_ptc_b`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`price`", "DataType": "INT", "Nullable": false } ],
           "Indexes": [ { "Name": "`pk_ss_ptc_b`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ],
           "CheckConstraints": [ { "Name": "`ck_x`", "Expression": "`price` >= 0" } ] }]
        """;

    [Test]
    public void TwoTablesWithTheSameCheckName_RedeployWithoutTouchingEitherCheck()
    {
        Deploy(TwoTablesWithTheSameCheckName);
        Exec("DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%ss_ptc_%'");

        Deploy(TwoTablesWithTheSameCheckName);

        Assert.That(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%ss_ptc_%'")), Is.Zero,
            "an unchanged redeploy must not drop or re-create either check");
    }

    [Test]
    public void TwoTablesWithTheSameCheckName_EachExtractsOnlyItsOwnCheck()
    {
        Deploy(TwoTablesWithTheSameCheckName);

        var a = Scalar($"CALL SchemaSmith_GenerateTableJSON('{_testDb}', 'ss_ptc_a')")?.ToString() ?? "";
        var b = Scalar($"CALL SchemaSmith_GenerateTableJSON('{_testDb}', 'ss_ptc_b')")?.ToString() ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(a, Does.Contain("qty").And.Not.Contain("price"));
            Assert.That(b, Does.Contain("price").And.Not.Contain("`qty`"));
        });
    }

    private static string TwoTablesWithTheSameForeignKeyName(string fkName) => $$"""
        [{ "Name": "`ss_ptc_p`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false } ],
           "Indexes": [ { "Name": "`pk_ss_ptc_p`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ] },
         { "Name": "`ss_ptc_a`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`a_parent`", "DataType": "INT", "Nullable": true } ],
           "Indexes": [ { "Name": "`pk_ss_ptc_a`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ],
           "ForeignKeys": [ { "Name": "`{{fkName}}`", "Columns": "`a_parent`", "RelatedTable": "`ss_ptc_p`", "RelatedColumns": "`id`" } ] },
         { "Name": "`ss_ptc_b`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`b_parent`", "DataType": "INT", "Nullable": true } ],
           "Indexes": [ { "Name": "`pk_ss_ptc_b`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ],
           "ForeignKeys": [ { "Name": "`{{fkName}}`", "Columns": "`b_parent`", "RelatedTable": "`ss_ptc_p`", "RelatedColumns": "`id`", "DeleteAction": "CASCADE" } ] }]
        """;

    private void RequireMariaDb121()
    {
        var version = VersionHelper.ParsePatchComparable(Scalar("SELECT VERSION()")?.ToString(), Platform.MariaDb) ?? 0;
        if (version < 120100)
            Assert.Ignore("Foreign-key names are unique per table from MariaDB 12.1; below that two tables cannot share one.");
    }

    [Test]
    public void TwoTablesWithTheSameForeignKeyName_DeployAndRedeployWithoutTouchingEither()
    {
        RequireMariaDb121();
        var json = TwoTablesWithTheSameForeignKeyName("fk_parent");
        Deploy(json);
        Exec("DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%ss_ptc_%'");

        Assert.DoesNotThrow(() => Deploy(json));

        Assert.That(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%ss_ptc_%'")), Is.Zero);
    }

    [Test]
    public void TwoTablesWithTheSameForeignKeyName_EachExtractsItsOwnForeignKey()
    {
        RequireMariaDb121();
        Deploy(TwoTablesWithTheSameForeignKeyName("fk_parent"));

        var a = Scalar($"CALL SchemaSmith_GenerateTableJSON('{_testDb}', 'ss_ptc_a')")?.ToString() ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(a, Does.Contain("a_parent").And.Not.Contain("b_parent"));
            Assert.That(a.Split("\"DeleteAction\"").Length - 1, Is.EqualTo(1), "table a declares exactly one foreign key");
            Assert.That(a, Does.Not.Contain("CASCADE"), "table b's delete rule must not reach table a");
        });
    }

    // Tables built outside SchemaSmith: unnamed foreign keys are named 1, 2, ... per table from 12.1, and every JSON
    // column carries a check named after the column. Extraction keeps those names, so redeploying what was extracted
    // must match them in place.
    [Test]
    public void UnnamedForeignKeysAndJsonColumns_ExtractAndRedeployWithoutTouchingAnything()
    {
        RequireMariaDb121();
        Exec($"CREATE TABLE `{_testDb}`.`ss_ptc_p` (id INT PRIMARY KEY)");
        Exec($"CREATE TABLE `{_testDb}`.`ss_ptc_a` (id INT PRIMARY KEY, a_parent INT, doc JSON, FOREIGN KEY (a_parent) REFERENCES `ss_ptc_p` (id))");
        Exec($"CREATE TABLE `{_testDb}`.`ss_ptc_b` (id INT PRIMARY KEY, b_parent INT, doc JSON, FOREIGN KEY (b_parent) REFERENCES `ss_ptc_p` (id) ON DELETE CASCADE)");

        string Extract(string table) => Scalar($"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{table}')")?.ToString() ?? "";
        var p = Extract("ss_ptc_p");
        var a = Extract("ss_ptc_a");
        var b = Extract("ss_ptc_b");

        Deploy($"[{p},{a},{b}]");

        Assert.Multiple(() =>
        {
            Assert.That(a.Split("\"DeleteAction\"").Length - 1, Is.EqualTo(1), "table a declares exactly one foreign key");
            Assert.That(a, Does.Not.Contain("CASCADE"), "table b's delete rule must not reach table a");
            Assert.That(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%ss_ptc_%'")), Is.Zero,
                "redeploying the extraction must not drop or re-create anything");
            Assert.That(Convert.ToInt64(Scalar(
                    $"SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME IN ('ss_ptc_a', 'ss_ptc_b') AND CONSTRAINT_TYPE = 'FOREIGN KEY'")),
                Is.EqualTo(2));
        });
    }
}
