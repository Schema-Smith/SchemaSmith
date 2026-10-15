// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// A MariaDB column's <c>CheckExpression</c> is a column-level check, which the engine names after the column. It stays
/// column-level across a redeploy, converges through <c>MODIFY COLUMN</c>, is never dropped as an undeclared table-level
/// check, and extracts back onto the column rather than into <c>CheckConstraints</c>. Every assertion reads the catalog.
/// Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ColumnCheckTests
{
    private const string Product = "CcTest";
    private const string Table = "cc_orders";
    private IDbConnection _connection;
    private string _db;
    private bool _recordsCheckLevel;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        // CHECK_CONSTRAINTS.LEVEL is MariaDB 10.5+; below it the name is the only evidence of the level.
        _recordsCheckLevel = (VersionHelper.ParsePatchComparable(Str("SELECT VERSION()"), Platform.MariaDb) ?? 0) >= 100500;
        DropOwnObjects();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try { DropOwnObjects(); }
        finally { _connection.Close(); _connection.Dispose(); }
    }

    [SetUp]
    public void SetUp()
    {
        DropOwnObjects();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{Table}%'");
    }

    private void DropOwnObjects() => Exec($"DROP TABLE IF EXISTS `{_db}`.`{Table}`");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string Str(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private int Count(string sql) => Convert.ToInt32(Str(sql) ?? "0");

    private void Deploy(string json) =>
        Exec($"CALL SchemaSmith_TableQuench('{Product}', '{_db}', '{json.Replace("'", "''")}', 0, 0, 0)");

    /// <param name="qtyCheck">The Qty column's CheckExpression, or null for none.</param>
    /// <param name="tableCheck">Whether the named table-level check is declared.</param>
    private static string Package(string qtyCheck = "`qty` >= 0", bool tableCheck = true) =>
        "[{ \"Name\": \"`" + Table + "`\", \"Engine\": \"InnoDB\", \"Columns\": ["
        + " { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"`qty`\", \"DataType\": \"INT\", \"Nullable\": true" + (qtyCheck == null ? "" : ", \"CheckExpression\": \"" + qtyCheck + "\"") + " },"
        + " { \"Name\": \"`price`\", \"DataType\": \"INT\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"`pk_" + Table + "`\", \"IndexColumns\": \"`id`\", \"PrimaryKey\": true, \"Unique\": true } ]"
        + (tableCheck ? ", \"CheckConstraints\": [ { \"Name\": \"`ck_" + Table + "_price`\", \"Expression\": \"`price` > 0 OR `qty` IS NULL\" } ]" : "")
        + " }]";

    /// <summary>Every CHECK on the table as <c>name=clause</c>, name-ordered; MariaDB names a column-level one after its column.</summary>
    private string Checks() => Str(
        $"SELECT GROUP_CONCAT(CONCAT(cc.CONSTRAINT_NAME, '=', cc.CHECK_CLAUSE) ORDER BY cc.CONSTRAINT_NAME SEPARATOR ';') " +
        $"FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc WHERE cc.CONSTRAINT_SCHEMA = '{_db}' AND cc.TABLE_NAME = '{Table}'");

    private string Level() => Str($"SELECT LEVEL FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_db}' AND TABLE_NAME = '{Table}' AND CONSTRAINT_NAME = 'qty'");

    private int ColumnAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'column' AND ActionType = '{action}' AND ObjectName LIKE '%{Table}%qty%'");

    private int CheckAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'constraint' AND ActionType = '{action}' AND ObjectName LIKE '%{Table}%'");

    private void ClearAudit() => Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{Table}%'");

    [Test]
    public void ADeclaredCheckExpression_IsAColumnLevelCheck_AndARedeployLeavesIt()
    {
        Deploy(Package());

        Assert.Multiple(() =>
        {
            Assert.That(Checks(), Is.EqualTo($"ck_{Table}_price=`price` > 0 or `qty` is null;qty=`qty` >= 0"), "the column's check is named after the column, beside the table-level one");
            if (_recordsCheckLevel) Assert.That(Level(), Is.EqualTo("Column"), "MariaDB records the level, and it is the column's");
        });

        ClearAudit();
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(Checks(), Is.EqualTo($"ck_{Table}_price=`price` > 0 or `qty` is null;qty=`qty` >= 0"), "a redeploy must not drop the column-level check as an undeclared one (MA-008)");
            Assert.That(ColumnAudits("modified"), Is.Zero, "and must not MODIFY the column");
            Assert.That(CheckAudits("dropped"), Is.Zero);
        });
    }

    [Test]
    public void AChangedCheckExpression_ConvergesThroughTheColumn()
    {
        Deploy(Package());
        ClearAudit();

        Deploy(Package(qtyCheck: "`qty` >= 1"));

        Assert.Multiple(() =>
        {
            Assert.That(Checks(), Does.Contain("qty=`qty` >= 1"));
            if (_recordsCheckLevel) Assert.That(Level(), Is.EqualTo("Column"));
            Assert.That(ColumnAudits("modified"), Is.EqualTo(1), "MODIFY COLUMN carries the new check");
        });
    }

    [Test]
    public void ARemovedCheckExpression_RemovesTheColumnLevelCheck()
    {
        Deploy(Package());

        Deploy(Package(qtyCheck: null));

        Assert.That(Checks(), Is.EqualTo($"ck_{Table}_price=`price` > 0 or `qty` is null"), "MODIFY without the check drops it, and the table-level one is untouched");
    }

    [Test]
    public void Extraction_WritesTheColumnsCheck_OnTheColumn_NotAsATableLevelEntry()
    {
        Deploy(Package());

        var table = Extract();
        Assert.Multiple(() =>
        {
            Assert.That(((MariaDbColumn)table.Columns.Single(c => c.Name == "`qty`")).CheckExpression, Is.EqualTo("`qty` >= 0"));
            Assert.That(((MariaDbColumn)table.Columns.Single(c => c.Name == "`price`")).CheckExpression, Is.Null);
            Assert.That(table.CheckConstraints.Select(c => c.Name), Is.EqualTo(new[] { $"ck_{Table}_price" }), "the column-level check is not a second table-level entry");
        });
    }

    private MariaDbTable Extract()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_db}', '{Table}')";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader[0];
        return (MariaDbTable)PlatformDeserializer.DeserializeTable(json, Platform.MariaDb);
    }
}
