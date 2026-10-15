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
/// A MariaDB <c>JSON</c> column is the engine's own rendering -- <c>longtext</c> in <c>utf8mb4_bin</c> with a column-level
/// <c>json_valid</c> check named after the column (10.4.3+) -- and the package says <c>JSON</c> and nothing else. A redeploy
/// leaves it alone, extraction folds the rendering back to <c>JSON</c>, and a TABLE-level check carrying the column's
/// name (what a redeploy of an older extraction created) is dropped before the column is modified: with only that the
/// column reads as text, and from 11.4.13 it blocks the MODIFY with 1826. Assertions read the catalog and the engine's
/// own JSON output. Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class JsonColumnTests
{
    private const string Product = "JcTest";
    private const string Table = "jc_docs";
    private IDbConnection _connection;
    private string _db;
    private long _version;

    private bool AddsJsonCheck => _version >= 100403;
    private bool RecordsCheckLevel => _version >= 100500;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _version = VersionHelper.ParsePatchComparable(Str("SELECT VERSION()"), Platform.MariaDb) ?? 0;
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

    private void Deploy(string json, bool whatIf = false) =>
        Exec($"CALL SchemaSmith_TableQuench('{Product}', '{_db}', '{json.Replace("'", "''")}', {(whatIf ? 1 : 0)}, 0, 0)");

    /// <param name="docType">The doc column's declared DataType.</param>
    private static string Package(string docType = "JSON") =>
        "[{ \"Name\": \"`" + Table + "`\", \"Engine\": \"InnoDB\", \"Columns\": ["
        + " { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"`doc`\", \"DataType\": \"" + docType + "\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"`pk_" + Table + "`\", \"IndexColumns\": \"`id`\", \"PrimaryKey\": true, \"Unique\": true } ] }]";

    private void InsertDocument() => Exec($"INSERT INTO `{_db}`.`{Table}` (id, doc) VALUES (1, '{{\"a\": 1}}')");

    /// <summary>The doc column's live type and collation.</summary>
    private string Shape() => Str(
        $"SELECT CONCAT(COLUMN_TYPE, ' ', COLLATION_NAME) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_db}' AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'doc'");

    /// <summary>Every CHECK on the table as <c>name=clause</c>, name-ordered; null when there is none.</summary>
    private string Checks() => Str(
        $"SELECT GROUP_CONCAT(CONCAT(cc.CONSTRAINT_NAME, '=', cc.CHECK_CLAUSE) ORDER BY cc.CONSTRAINT_NAME SEPARATOR ';') " +
        $"FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc WHERE cc.CONSTRAINT_SCHEMA = '{_db}' AND cc.TABLE_NAME = '{Table}'");

    /// <summary>The level(s) of the check(s) named after the column, 10.5+.</summary>
    private string Levels() => Str(
        $"SELECT GROUP_CONCAT(LEVEL ORDER BY LEVEL SEPARATOR ';') FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_db}' AND TABLE_NAME = '{Table}' AND CONSTRAINT_NAME = 'doc'");

    /// <summary>What the engine makes of the column: an object when it is JSON, an escaped string when it is text.</summary>
    private string JsonObject() => Str($"SELECT JSON_OBJECT('doc', doc) FROM `{_db}`.`{Table}` WHERE id = 1");

    private int ColumnAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'column' AND ActionType = '{action}' AND ObjectName LIKE '%{Table}%doc%'");

    private int CheckAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'constraint' AND ActionType = '{action}' AND ObjectName LIKE '%{Table}%'");

    private void ClearAudit() => Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{Table}%'");

    [Test]
    public void ADeclaredJsonColumn_IsTheEnginesOwnRendering_AndARedeployLeavesIt()
    {
        Deploy(Package());
        InsertDocument();

        Assert.Multiple(() =>
        {
            Assert.That(Shape(), Is.EqualTo("longtext utf8mb4_bin"), "MariaDB stores JSON as longtext in utf8mb4_bin");
            if (AddsJsonCheck)
                Assert.That(Checks(), Is.EqualTo("doc=json_valid(`doc`)"), "with a json_valid check it names after the column");
            else
                Assert.That(Checks(), Is.Null, "and below 10.4.3 with no check at all");
            if (RecordsCheckLevel)
            {
                Assert.That(Levels(), Is.EqualTo("Column"));
                Assert.That(JsonObject(), Does.Not.Contain("\\\""), "the engine reads the column as JSON: the value is an object, not an escaped string");
            }
        });

        ClearAudit();
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(ColumnAudits("modified"), Is.Zero, "the declared JSON is compared with the engine's rendering, not with the word longtext (MA-006)");
            Assert.That(CheckAudits("dropped"), Is.Zero, "and the engine's check is not an undeclared one");
            Assert.That(Shape(), Is.EqualTo("longtext utf8mb4_bin"));
        });
    }

    [Test]
    public void Extraction_FoldsTheRenderingBackToJson_WithNoCheckOfItsOwn()
    {
        Deploy(Package());

        var table = Extract();
        var doc = (MariaDbColumn)table.Columns.Single(c => c.Name == "`doc`");
        Assert.Multiple(() =>
        {
            if (AddsJsonCheck)
            {
                Assert.That(doc.DataType, Is.EqualTo("JSON"));
                Assert.That(doc.CheckExpression, Is.Null, "the engine's own check is implied by JSON");
                Assert.That(doc.CharacterSet, Is.Null, "as are its character set");
                Assert.That(doc.Collation, Is.Null, "and collation");
            }
            else
                Assert.That(doc.DataType, Is.EqualTo("longtext"), "below 10.4.3 nothing in the catalog tells a JSON column from a longtext");
            Assert.That(table.CheckConstraints, Is.Empty, "and the check is not a table-level entry either");
        });
    }

    [Test]
    public void ATableLevelCheckCarryingTheColumnsName_IsDroppedBeforeTheColumnIsModified_AndTheColumnReadsAsJson()
    {
        if (!RecordsCheckLevel) Assert.Ignore("CHECK_CONSTRAINTS.LEVEL, which tells the two apart, is MariaDB 10.5+");
        // The shape a redeploy of an older extraction left: the engine's rendering, but with the check at TABLE level.
        Deploy(Package("LONGTEXT"));
        Exec($"ALTER TABLE `{_db}`.`{Table}` MODIFY COLUMN `doc` LONGTEXT COLLATE utf8mb4_bin NULL, ADD CONSTRAINT `doc` CHECK (json_valid(`doc`))");
        InsertDocument();
        Assert.Multiple(() =>
        {
            Assert.That(Levels(), Is.EqualTo("Table"), "precondition");
            Assert.That(JsonObject(), Does.Contain("\\\""), "precondition: with only a table-level check the engine reads the column as text (MA-007)");
        });

        ClearAudit();
        Deploy(Package(), whatIf: true);
        Assert.Multiple(() =>
        {
            Assert.That(Str($"SELECT GROUP_CONCAT(Message SEPARATOR '\\n') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%{Table}%'"),
                Does.Contain("DROP CONSTRAINT `doc`").And.Contain("MODIFY COLUMN `doc` JSON"), "the preview shows the drop and the modify");
            Assert.That(Str($"SELECT GROUP_CONCAT(Message SEPARATOR '\n') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%{Table}%'"),
                Does.Not.Contain("Drop check constraint:"), "and only shows it");
            Assert.That(CheckAudits("wouldDrop"), Is.EqualTo(1));
            Assert.That(ColumnAudits("wouldModify"), Is.EqualTo(1));
            Assert.That(Levels(), Is.EqualTo("Table"), "and changes nothing");
        });

        ClearAudit();
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(Checks(), Is.EqualTo("doc=json_valid(`doc`)"), "one check, the column's own");
            Assert.That(Levels(), Is.EqualTo("Column"), "the table-level one is gone (MA-090: 11.4.13+ refuses the duplicate name outright)");
            Assert.That(JsonObject(), Does.Not.Contain("\\\""), "and the column reads as JSON again");
            Assert.That(CheckAudits("dropped"), Is.EqualTo(1));
            Assert.That(ColumnAudits("modified"), Is.EqualTo(1));
            Assert.That(Str($"SELECT GROUP_CONCAT(Message SEPARATOR '\n') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%{Table}%'"),
                Does.Contain("Drop check constraint: ALTER TABLE").And.Contain("DROP CONSTRAINT `doc`"), "the drop is reported like the modify");
        });
    }

    [Test]
    public void AColumnChangingToAndFromJson_ConvergesThroughTheColumn()
    {
        Deploy(Package("VARCHAR(100)"));
        ClearAudit();

        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(Shape(), Is.EqualTo("longtext utf8mb4_bin"));
            Assert.That(ColumnAudits("modified"), Is.EqualTo(1));
            if (AddsJsonCheck) Assert.That(Checks(), Is.EqualTo("doc=json_valid(`doc`)"));
        });

        ClearAudit();
        Deploy(Package("LONGTEXT"));
        Assert.Multiple(() =>
        {
            Assert.That(Shape(), Does.StartWith("longtext "));
            Assert.That(Checks(), Is.Null, "a MODIFY without JSON is the only way the engine's check goes: it cannot be dropped by name (MA-074)");
            Assert.That(ColumnAudits("modified"), Is.EqualTo(AddsJsonCheck ? 1 : 0), "and below 10.4.3 there is no check, so nothing differs");
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
