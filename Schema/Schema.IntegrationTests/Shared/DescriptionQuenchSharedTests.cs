// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Shared table/column/index <c>Description</c> round-trip and drift tests for the MySQL/MariaDb family, where the
/// description is the object's <c>COMMENT</c>. A declared value is applied, a change (nothing else differing) is detected
/// and applied, and extraction writes <c>Description</c>. <c>Comment</c>, the pre-2.8 spelling, still deploys. The MySQL
/// and MariaDb subclasses supply the platform + fixture accessors; every [Test] body here runs on both engines.
/// </summary>
public abstract class DescriptionQuenchSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private const string TableName = "description_quench_test";
    private const string PkIndexName = "pk_description_quench_test";
    private const string SecondaryIndexName = "ix_val_description_quench_test";
    private const string Product = "DescriptionQuenchProduct";

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
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
        DropTestTable();
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
    }

    [TearDown]
    public void TearDown() => DropTestTable();

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

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    private static string BuildTableJson(string tableDescription, string columnDescription, string indexDescription)
    {
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Description = tableDescription,
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`val`", DataType = "INT", Nullable = false, Default = "0", Description = columnDescription }
            ],
            Indexes =
            [
                new MySqlIndex { Name = $"`{PkIndexName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" },
                new MySqlIndex { Name = $"`{SecondaryIndexName}`", IndexColumns = "`val`", Description = indexDescription }
            ]
        };
        return "[" + JsonConvert.SerializeObject(table) + "]";
    }

    private void DeployJson(string json) =>
        Exec($"CALL SchemaSmith_TableQuench('{Product}', '{_testDb}', '{json.Replace("'", "''")}', 0, 0, 0)");

    private void Deploy(string tableDescription, string columnDescription, string indexDescription) =>
        DeployJson(BuildTableJson(tableDescription, columnDescription, indexDescription));

    private string ExtractJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')";
        var json = "";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            json += reader[0];
        return json;
    }

    private MySqlTable Extract() => PlatformDeserializer.DeserializeTable(ExtractJson(), Platform) as MySqlTable;

    private string TableComment() => ScalarStr(
        $@"SELECT TABLE_COMMENT FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'");

    private string ColumnComment() => ScalarStr(
        $@"SELECT COLUMN_COMMENT FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'val'");

    private string IndexComment() => ScalarStr(
        $@"SELECT MAX(INDEX_COMMENT) FROM INFORMATION_SCHEMA.STATISTICS
           WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}' AND INDEX_NAME = '{SecondaryIndexName}'");

    private long ColumnModifiedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'modified' AND ObjectName LIKE '%{TableName}.val%'");

    private long IndexCreatedAuditCount() => Scalar(
        $@"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
           WHERE ActionType = 'created' AND ObjectType = 'index' AND ObjectName LIKE '%{TableName}.{SecondaryIndexName}%'");

    [Test]
    public void TableColumnAndIndexDescriptions_AreAppliedOnCreate_AndRoundTripThroughExtraction()
    {
        Deploy(tableDescription: "table description", columnDescription: "column description", indexDescription: "index description");

        Assert.Multiple(() =>
        {
            Assert.That(TableComment(), Is.EqualTo("table description"), "The declared table Description must be applied as the COMMENT on CREATE TABLE.");
            Assert.That(ColumnComment(), Is.EqualTo("column description"), "The declared column Description must be applied as the COMMENT on CREATE TABLE.");
            Assert.That(IndexComment(), Is.EqualTo("index description"), "The declared index Description must be applied as the COMMENT on CREATE INDEX.");
        });

        var json = ExtractJson();
        var table = PlatformDeserializer.DeserializeTable(json, Platform) as MySqlTable;
        Assert.That(table, Is.Not.Null);
        var val = (MySqlColumn)table!.Columns.Find(c => c.Name.Contains("val"));
        var idx = (MySqlIndex)table.Indexes.Find(i => i.Name.Contains(SecondaryIndexName));

        Assert.Multiple(() =>
        {
            Assert.That(table.Description, Is.EqualTo("table description"), "Extraction must round-trip the declared table Description.");
            Assert.That(val, Is.Not.Null);
            Assert.That(val!.Description, Is.EqualTo("column description"), "Extraction must round-trip the declared column Description.");
            Assert.That(idx, Is.Not.Null);
            Assert.That(idx!.Description, Is.EqualTo("index description"), "Extraction must round-trip the declared index Description.");
            Assert.That(json, Does.Not.Contain("\"Comment\""), "Extraction writes the current spelling, never the deprecated one.");
            Assert.That(table.Comment, Is.Null);
        });
    }

    [Test]
    public void TheDeprecatedCommentKey_StillDeploys_AndDescriptionWinsWhenBothArePresent()
    {
        var json = $$"""
            [{ "Name": "`{{TableName}}`", "Engine": "InnoDB", "Comment": "from the alias",
               "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false, "AutoIncrement": true },
                            { "Name": "`val`", "DataType": "INT", "Nullable": false, "Default": "0", "Description": "from the key", "Comment": "from the alias" } ],
               "Indexes": [ { "Name": "`{{PkIndexName}}`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" },
                            { "Name": "`{{SecondaryIndexName}}`", "IndexColumns": "`val`", "Comment": "from the alias" } ] }]
            """;
        DeployJson(json);

        Assert.Multiple(() =>
        {
            Assert.That(TableComment(), Is.EqualTo("from the alias"), "Comment, the pre-2.8 spelling, is still honoured.");
            Assert.That(ColumnComment(), Is.EqualTo("from the key"), "Description wins when both are present.");
            Assert.That(IndexComment(), Is.EqualTo("from the alias"));
        });
    }

    [Test]
    public void TableDescriptionChange_DifferingOnlyInDescription_IsDetectedAndApplied()
    {
        Deploy(tableDescription: "before", columnDescription: null, indexDescription: null);
        Assert.That(TableComment(), Is.EqualTo("before"), "Sanity: table comment starts at the first declared value.");

        Deploy(tableDescription: "after", columnDescription: null, indexDescription: null);

        Assert.That(TableComment(), Is.EqualTo("after"), "A table description change (nothing else differs) must be detected and applied.");
    }

    [Test]
    public void ColumnDescriptionChange_DifferingOnlyInDescription_IsDetectedAndApplied()
    {
        Deploy(tableDescription: null, columnDescription: "before", indexDescription: null);
        Assert.That(ColumnComment(), Is.EqualTo("before"), "Sanity: column comment starts at the first declared value.");

        Deploy(tableDescription: null, columnDescription: "after", indexDescription: null);

        Assert.Multiple(() =>
        {
            Assert.That(ColumnComment(), Is.EqualTo("after"), "A column description change (nothing else differs) must be detected and applied.");
            Assert.That(ColumnModifiedAuditCount(), Is.GreaterThanOrEqualTo(1), "The description-only change must record a 'modified' manifest row.");
        });
    }

    [Test]
    public void IndexDescriptionChange_DifferingOnlyInDescription_IsDetectedAndApplied()
    {
        Deploy(tableDescription: null, columnDescription: null, indexDescription: "before");
        Assert.That(IndexComment(), Is.EqualTo("before"), "Sanity: index comment starts at the first declared value.");

        Deploy(tableDescription: null, columnDescription: null, indexDescription: "after");

        Assert.Multiple(() =>
        {
            Assert.That(IndexComment(), Is.EqualTo("after"), "An index description change (nothing else differs) must be detected and applied.");
            Assert.That(IndexCreatedAuditCount(), Is.GreaterThanOrEqualTo(1),
                "The description-only change is applied via the existing drop+recreate path for modified indexes, which must record the recreate.");
        });
    }

    [Test]
    public void DescriptionsContainingASingleQuote_SurviveRoundTrip()
    {
        const string quoted = "Bob's data";
        Deploy(tableDescription: quoted, columnDescription: quoted, indexDescription: quoted);

        Assert.Multiple(() =>
        {
            Assert.That(TableComment(), Is.EqualTo(quoted), "A table description containing a single quote must round-trip through the DDL's own escaping.");
            Assert.That(ColumnComment(), Is.EqualTo(quoted), "A column description containing a single quote must round-trip through the DDL's own escaping.");
            Assert.That(IndexComment(), Is.EqualTo(quoted), "An index description containing a single quote must round-trip through the DDL's own escaping.");
        });

        var table = Extract();
        var val = (MySqlColumn)table!.Columns.Find(c => c.Name.Contains("val"));
        var idx = (MySqlIndex)table.Indexes.Find(i => i.Name.Contains(SecondaryIndexName));

        Assert.Multiple(() =>
        {
            Assert.That(table.Description, Is.EqualTo(quoted));
            Assert.That(val!.Description, Is.EqualTo(quoted));
            Assert.That(idx!.Description, Is.EqualTo(quoted));
        });
    }

    [Test]
    public void TableWithNoDescriptions_IsUnaffected_AndRedeployIsIdempotent()
    {
        Deploy(tableDescription: null, columnDescription: null, indexDescription: null);

        Assert.Multiple(() =>
        {
            Assert.That(string.IsNullOrEmpty(TableComment()), Is.True, "No declared table description must leave TABLE_COMMENT empty.");
            Assert.That(string.IsNullOrEmpty(ColumnComment()), Is.True, "No declared column description must leave COLUMN_COMMENT empty.");
            Assert.That(string.IsNullOrEmpty(IndexComment()), Is.True, "No declared index description must leave INDEX_COMMENT empty.");
        });

        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");

        Deploy(tableDescription: null, columnDescription: null, indexDescription: null);

        Assert.Multiple(() =>
        {
            Assert.That(string.IsNullOrEmpty(TableComment()), Is.True, "A second no-description deploy must not introduce a comment.");
            Assert.That(ColumnModifiedAuditCount(), Is.EqualTo(0), "A second no-description deploy must not record a spurious 'modified' row (non-idempotency bug).");
            Assert.That(IndexCreatedAuditCount(), Is.EqualTo(0), "A second no-description deploy must not drop+recreate the unchanged index (non-idempotency bug).");
        });
    }

    // SchemaSmith_IndexOnlyQuench is a distinct procedure carrying its own copy of the create/modify-index logic;
    // exercise it directly (SchemaSmith_TableQuench routes indexes through SchemaSmith_MissingIndexesAndConstraintsQuench).
    [Test]
    public void IndexOnlyQuench_CreatesIndexWithDescription_AndDetectsDescriptionChange()
    {
        var baseTable = new MySqlTable
        {
            Name = $"`{TableName}`",
            Engine = "InnoDB",
            Columns =
            [
                new MySqlColumn { Name = "`id`", DataType = "INT", Nullable = false, AutoIncrement = true },
                new MySqlColumn { Name = "`val`", DataType = "INT", Nullable = false, Default = "0" }
            ],
            Indexes =
            [
                new MySqlIndex { Name = $"`{PkIndexName}`", PrimaryKey = true, Unique = true, IndexColumns = "`id`" }
            ]
        };
        DeployJson("[" + JsonConvert.SerializeObject(baseTable) + "]");

        DeployIndexOnly(indexDescription: "index-only before");
        Assert.That(IndexComment(), Is.EqualTo("index-only before"), "SchemaSmith_IndexOnlyQuench must apply a declared index Description on create.");

        DeployIndexOnly(indexDescription: "index-only after");
        Assert.That(IndexComment(), Is.EqualTo("index-only after"),
            "SchemaSmith_IndexOnlyQuench must detect and apply an index description change (nothing else differs) the same way SchemaSmith_MissingIndexesAndConstraintsQuench does.");
    }

    private void DeployIndexOnly(string indexDescription)
    {
        // IndexOnlyQuench reads the _SchemaSmith_Indexes temp table populated by ParseTableJson, so the
        // two calls run back-to-back on the same connection/session, exactly as the product code does.
        var table = new MySqlTable
        {
            Name = $"`{TableName}`",
            Indexes =
            [
                new MySqlIndex { Name = $"`{SecondaryIndexName}`", IndexColumns = "`val`", Description = indexDescription }
            ]
        };
        var json = ("[" + JsonConvert.SerializeObject(table) + "]").Replace("'", "''");
        Exec($"CALL SchemaSmith_ParseTableJson('{_testDb}', '{json}')");
        Exec($"CALL SchemaSmith_IndexOnlyQuench('{Product}', '{_testDb}', 0, 0, 0)");
    }
}
