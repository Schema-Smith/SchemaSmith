// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for FULLTEXT index deployment via ParseTableJson and IndexOnlyQuench.
/// Verifies that FULLTEXT indexes defined in table JSON are correctly parsed and created.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_FullTextIndexSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection = null!;
    private string _testTableName = null!;
    private string _testDb = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        _testTableName = $"_test_ft_{Guid.NewGuid():N}"[..30];

        // Create test table with TEXT columns suitable for FULLTEXT indexing
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{_testTableName}` (
                id INT AUTO_INCREMENT PRIMARY KEY,
                title VARCHAR(200) NOT NULL,
                content TEXT,
                summary TEXT
            )";
        command.ExecuteNonQuery();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{_testTableName}`";
            command.ExecuteNonQuery();
        }
        catch { /* ignore cleanup errors */ }

        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void ShouldDeployFullTextIndex()
    {
        // Arrange - Parse table JSON with a FULLTEXT index defined, then run full table quench path
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Columns"": [
                {{""Name"": ""`id`"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true}},
                {{""Name"": ""`title`"", ""DataType"": ""VARCHAR(200)"", ""Nullable"": false}},
                {{""Name"": ""`content`"", ""DataType"": ""TEXT""}},
                {{""Name"": ""`summary`"", ""DataType"": ""TEXT""}}
            ],
            ""Indexes"": [
                {{""Name"": ""`PRIMARY`"", ""IndexColumns"": ""`id`"", ""PrimaryKey"": true}}
            ],
            ""FullTextIndexes"": [
                {{""Name"": ""ft_content"", ""Columns"": ""content""}}
            ]
        }}]";

        // Parse JSON and run IndexOnlyQuench (which handles FULLTEXT indexes)
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Act - IndexOnlyQuench creates the FULLTEXT index
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Assert - FULLTEXT index should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'ft_content'
              AND INDEX_TYPE = 'FULLTEXT'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.GreaterThan(0), "FULLTEXT index should have been created");
    }

    [Test]
    public void ShouldDeployFullTextIndexViaIndexOnly()
    {
        // Arrange - Use IndexOnly path explicitly (no column definitions needed)
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""FullTextIndexes"": [
                {{""Name"": ""ft_title"", ""Columns"": ""title""}}
            ]
        }}]";

        // Parse JSON
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Act - IndexOnlyQuench
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Assert - FULLTEXT index should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'ft_title'
              AND INDEX_TYPE = 'FULLTEXT'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.GreaterThan(0), "FULLTEXT index should have been created via IndexOnly path");
    }

    [Test]
    public void ShouldDeployFullTextIndexWithParser()
    {
        // The ngram full-text parser plugin ships with MySQL 8.0 but not MariaDB — MariaDB raises
        // "Function 'ngram' is not defined" (error 1128). WITH PARSER ngram is therefore MySQL-only;
        // the gated-variant test below exercises the ShouldApplyExpression path that lets a product
        // ship a non-ngram FULLTEXT variant for MariaDB targets.
        if (Platform == Platform.MariaDb)
            Assert.Ignore("FULLTEXT WITH PARSER ngram is a MySQL-only feature; MariaDB has no ngram parser plugin.");

        // Arrange - FULLTEXT index with ngram parser (available in MySQL 8.0+)
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""FullTextIndexes"": [
                {{""Name"": ""ft_content_ngram"", ""Columns"": ""content"", ""Parser"": ""ngram""}}
            ]
        }}]";

        // Parse JSON
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Assert - FULLTEXT index should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'ft_content_ngram'
              AND INDEX_TYPE = 'FULLTEXT'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.GreaterThan(0), "FULLTEXT index with parser should have been created");

        // Verify the parser was applied by checking INFORMATION_SCHEMA.INNODB_INDEXES
        // or by attempting to use the index — the parser attribute is not exposed in STATISTICS.
        // For now, existence of the index confirms the WITH PARSER clause didn't cause an error.
    }

    [Test]
    public void ShouldDeployMultiColumnFullTextIndex()
    {
        // Arrange - FULLTEXT index spanning multiple columns
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""FullTextIndexes"": [
                {{""Name"": ""ft_multi"", ""Columns"": ""title, content, summary""}}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Assert - FULLTEXT index should exist with 3 columns
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'ft_multi'
              AND INDEX_TYPE = 'FULLTEXT'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.EqualTo(3), "FULLTEXT index should span 3 columns");
    }

    [Test]
    public void ShouldSelectGatedFullTextVariant()
    {
        var variantTableName = $"{_testTableName}_v";
        using var command = _connection.CreateCommand();

        // Create the variant table (SetUp only creates _testTableName)
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{variantTableName}` (
                `id` INT AUTO_INCREMENT PRIMARY KEY,
                `content` TEXT
            )";
        command.ExecuteNonQuery();

        try
        {
            var tableJson = $@"[{{
                ""Name"": ""`{variantTableName}`"",
                ""Columns"": [
                    {{""Name"": ""`id`"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true}},
                    {{""Name"": ""`content`"", ""DataType"": ""TEXT""}}
                ],
                ""Indexes"": [
                    {{""Name"": ""`PRIMARY`"", ""IndexColumns"": ""`id`"", ""PrimaryKey"": true}}
                ],
                ""FullTextIndexes"": [
                    {{""Name"": ""ft_variant"", ""Columns"": ""content"", ""ShouldApplyExpression"": ""1 = 1""}},
                    {{""Name"": ""ft_variant"", ""Columns"": ""content"", ""Parser"": ""ngram"", ""ShouldApplyExpression"": ""1 = 0""}}
                ]
            }}]";

            command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
            TransientRetry.Run(() => command.ExecuteNonQuery());

            command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
            TransientRetry.Run(() => command.ExecuteNonQuery());

            // Exactly one FULLTEXT index — the gated-true (non-ngram) variant was selected
            command.CommandText = $@"
                SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{variantTableName}'
                  AND INDEX_TYPE = 'FULLTEXT'";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1),
                "Exactly one FULLTEXT index should be deployed — the variant with ShouldApplyExpression '1 = 1'");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{variantTableName}`";
            command.ExecuteNonQuery();
        }
    }

    private string FullTableJson(string fullText) => $$"""
        [{ "Name": "`{{_testTableName}}`",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false, "AutoIncrement": true },
                        { "Name": "`title`", "DataType": "VARCHAR(200)", "Nullable": false },
                        { "Name": "`content`", "DataType": "TEXT" }, { "Name": "`summary`", "DataType": "TEXT" } ],
           "Indexes": [ { "Name": "`PRIMARY`", "IndexColumns": "`id`", "PrimaryKey": true } ],
           "FullTextIndexes": [ {{fullText}} ] }]
        """;

    // The normal table quench, with both by-absence drops on: DropUnknownIndexes here, removed-from-product inside.
    private void FullQuench(IDbCommand command, string json, int dropUnknownIndexes = 1)
    {
        command.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        command.ExecuteNonQuery();
        command.CommandText = $"CALL SchemaSmith_TableQuench('FullTextProduct', '{_testDb}', '{json.Replace("'", "''")}', 0, {dropUnknownIndexes}, 0)";
        TransientRetry.Run(() => command.ExecuteNonQuery());
    }

    private string FullText(IDbCommand command)
    {
        command.CommandText = $"""
            SELECT IFNULL(GROUP_CONCAT(CONCAT(INDEX_NAME, '(', cols, ')', IF(cmt <> '', CONCAT(' ', cmt), '')) ORDER BY INDEX_NAME SEPARATOR ' '), '')
              FROM (SELECT INDEX_NAME, GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) cols, MAX(INDEX_COMMENT) cmt
                      FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{_testTableName}' AND INDEX_TYPE = 'FULLTEXT'
                     GROUP BY INDEX_NAME) f
            """;
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    private string IndexAudits(IDbCommand command)
    {
        command.CommandText = $"SELECT IFNULL(GROUP_CONCAT(ActionType ORDER BY ActionType), '') FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType IN ('fullTextIndex', 'index') AND ObjectName LIKE '%{_testTableName}%'";
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    // #530: a normal deploy never created a table's FullTextIndexes (only an index-only quench did), dropped one it had
    // created earlier as removed from the package, and never compared a changed one (MY-025).
    [Test]
    public void TheFullTableQuench_CreatesAFullTextIndex_KeepsIt_AndReCreatesItWhenItsColumnsOrCommentChange()
    {
        using var command = _connection.CreateCommand();
        FullQuench(command, FullTableJson("""{ "Name": "ft_content", "Columns": "content" }"""));
        Assert.Multiple(() =>
        {
            Assert.That(FullText(command), Is.EqualTo("ft_content(content)"), "created by the normal deploy");
            Assert.That(IndexAudits(command), Is.EqualTo("created"));
        });

        FullQuench(command, FullTableJson("""{ "Name": "ft_content", "Columns": "content" }"""));
        Assert.Multiple(() =>
        {
            Assert.That(FullText(command), Is.EqualTo("ft_content(content)"), "kept: it is in the package, so it is neither removed nor unknown");
            Assert.That(IndexAudits(command), Is.Empty, "and a redeploy touches nothing");
        });

        FullQuench(command, FullTableJson("""{ "Name": "ft_content", "Columns": "content, summary" }"""));
        Assert.That(FullText(command), Is.EqualTo("ft_content(content,summary)"), "its columns changed");

        FullQuench(command, FullTableJson("""{ "Name": "ft_content", "Columns": "content, summary", "Comment": "search" }"""));
        Assert.That(FullText(command), Is.EqualTo("ft_content(content,summary) search"), "its comment changed");

        FullQuench(command, FullTableJson("""{ "Name": "ft_content", "Columns": "content, summary", "Comment": "search" }"""));
        Assert.That(IndexAudits(command), Is.Empty, "then a redeploy is a no-op");
    }

    [Test]
    public void TheFullTableQuench_KeepsADeclaredFullTextIndexBuiltByHand_UnderDropUnknownIndexes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"ALTER TABLE `{_testDb}`.`{_testTableName}` ADD FULLTEXT INDEX ft_summary (summary)";
        command.ExecuteNonQuery();

        FullQuench(command, FullTableJson("""{ "Name": "ft_summary", "Columns": "summary" }"""));
        Assert.Multiple(() =>
        {
            Assert.That(FullText(command), Is.EqualTo("ft_summary(summary)"));
            Assert.That(IndexAudits(command), Is.Empty, "the package declares it, so it is not unknown");
        });
    }

    [Test]
    public void TheIndexOnlyQuench_ReCreatesAFullTextIndexWhoseColumnsChanged()
    {
        using var command = _connection.CreateCommand();
        void IndexOnly(string columns)
        {
            var json = $$"""[{ "Name": "`{{_testTableName}}`", "FullTextIndexes": [ { "Name": "ft_io", "Columns": "{{columns}}" } ] }]""";
            command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{json.Replace("'", "''")}')";
            TransientRetry.Run(() => command.ExecuteNonQuery());
            command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
            TransientRetry.Run(() => command.ExecuteNonQuery());
        }
        IndexOnly("content");
        command.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        command.ExecuteNonQuery();
        IndexOnly("content");
        Assert.That(IndexAudits(command), Is.Empty, "an unchanged redeploy no longer drops and re-creates it");
        IndexOnly("content,title");
        Assert.That(FullText(command), Is.EqualTo("ft_io(content,title)"));
    }

    [Test]
    public void ShouldTrackFullTextIndexInProductOwnership()
    {
        // Arrange
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""FullTextIndexes"": [
                {{""Name"": ""ft_owned"", ""Columns"": ""content""}}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        TransientRetry.Run(() => command.ExecuteNonQuery());

        // Assert - FULLTEXT index should be tracked in ProductOwnership
        command.CommandText = $@"
            SELECT COUNT(*) FROM `{MainDb}`.SchemaSmith_ProductOwnership
            WHERE ProductName = 'TestProduct'
              AND ObjectSchema = '{_testDb}'
              AND ObjectType = 'INDEX'
              AND ObjectName = '{_testTableName}.ft_owned'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.EqualTo(1), "FULLTEXT index should be tracked in ProductOwnership");
    }
}
