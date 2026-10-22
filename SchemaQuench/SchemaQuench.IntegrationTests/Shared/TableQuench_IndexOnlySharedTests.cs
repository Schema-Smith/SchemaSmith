// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for Index Only quench functionality.
/// Tests that IndexOnlyQuench correctly handles indexes without touching table structure.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_IndexOnlySharedTests
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
        _testTableName = $"_test_idx_{Guid.NewGuid():N}".Substring(0, 30);

        // Create test table with a primary key and one column
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{_testTableName}` (
                id INT AUTO_INCREMENT PRIMARY KEY,
                code VARCHAR(20) NOT NULL,
                name VARCHAR(100) NOT NULL,
                value DECIMAL(10,2) DEFAULT 0.00,
                active TINYINT(1) DEFAULT 1
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
    public void IndexOnlyQuench_ShouldAddMissingIndex()
    {
        // Arrange - Parse table JSON with an index defined
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_code`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        // Parse the JSON to create temp tables
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - Call IndexOnlyQuench
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - Index should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_code'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.GreaterThan(0), "Index should have been created");
    }

    [Test]
    public void IndexOnlyQuench_ShouldAddUniqueIndex()
    {
        // Arrange
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_code_unique`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": true,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - Unique index should exist
        command.CommandText = $@"
            SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_code_unique'
              AND SEQ_IN_INDEX = 1";
        var nonUnique = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(nonUnique, Is.EqualTo(0), "Index should be unique (NON_UNIQUE=0)");
    }

    [Test]
    public void IndexOnlyQuench_ShouldNotCreateTableOrColumn()
    {
        // Arrange - Define a column that doesn't exist
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Columns"": [
                {{
                    ""Name"": ""`new_column`"",
                    ""DataType"": ""VARCHAR(50)"",
                    ""Nullable"": true
                }}
            ],
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_code`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - IndexOnlyQuench should NOT add the column
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - new_column should NOT exist (IndexOnly doesn't touch columns)
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND COLUMN_NAME = 'new_column'";
        var columnCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(columnCount, Is.EqualTo(0), "Column should NOT have been created by IndexOnlyQuench");

        // But the index should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_code'";
        var indexCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(indexCount, Is.GreaterThan(0), "Index should have been created");
    }

    [Test]
    public void IndexOnlyQuench_ShouldDropUnknownIndex_WhenEnabled()
    {
        // Arrange - Create an index that won't be in the definition
        using var command = _connection.CreateCommand();
        command.CommandText = $"CREATE INDEX `idx_to_drop` ON `{_testDb}`.`{_testTableName}` (`name`)";
        command.ExecuteNonQuery();

        // Register it in ProductOwnership
        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_to_drop')";
        command.ExecuteNonQuery();

        // Define table with only idx_code (not idx_to_drop)
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_code`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - Call with DropUnknownIndexes=1
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 1, 1)";
        command.ExecuteNonQuery();

        // Assert - idx_to_drop should be gone
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_to_drop'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.EqualTo(0), "Unknown index should have been dropped");

        // And idx_code should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_code'";
        var codeIndexCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(codeIndexCount, Is.GreaterThan(0), "Defined index should exist");
    }

    [Test]
    public void IndexOnlyQuench_ShouldRenameIndex()
    {
        // Arrange - Create an index with old name
        using var command = _connection.CreateCommand();
        command.CommandText = $"CREATE INDEX `idx_old_name` ON `{_testDb}`.`{_testTableName}` (`code`)";
        command.ExecuteNonQuery();

        // Register it in ProductOwnership
        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_old_name')";
        command.ExecuteNonQuery();

        // Define table with same columns but new name
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_new_name`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - Old name should be gone, new name should exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_old_name'";
        var oldCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(oldCount, Is.EqualTo(0), "Old index name should not exist");

        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_new_name'";
        var newCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(newCount, Is.GreaterThan(0), "New index name should exist");
    }

    [Test]
    public void IndexOnlyQuench_ShouldRenameIndex_PreservingPrefixLengthAndUniqueness()
    {
        // Regression coverage for the below-MySQL-8.0 / below-MariaDB-10.5.2 degrade path
        // (SchemaSmith_BuildIndexRenameClause rebuilding DROP/ADD from _SchemaSmith_IdxDetectSnap
        // instead of RENAME INDEX): a prefix-length, unique index must keep its SUB_PART and
        // uniqueness across a rename, not just its column list. On engines new enough to support
        // RENAME INDEX this exercises the metadata-only early-return path instead -- still valid
        // coverage, just not the path this task changed (that's the MariaDB 10.2 floor lane's job).
        using var command = _connection.CreateCommand();
        command.CommandText = $"CREATE UNIQUE INDEX `idx_prefix_old` ON `{_testDb}`.`{_testTableName}` (`code`(5))";
        command.ExecuteNonQuery();

        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_prefix_old')";
        command.ExecuteNonQuery();

        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_prefix_new`"",
                    ""IndexColumns"": ""`code`(5)"",
                    ""Unique"": true,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_prefix_old'";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0),
            "Old index name should not exist");

        command.CommandText = $@"
            SELECT SUB_PART, NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_prefix_new'
            ORDER BY SEQ_IN_INDEX";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "Renamed index should exist");
        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToInt32(reader["SUB_PART"]), Is.EqualTo(5),
                "The prefix length must survive the rename -- the degrade path rebuilds the index from catalog metadata.");
            Assert.That(Convert.ToInt32(reader["NON_UNIQUE"]), Is.EqualTo(0),
                "Uniqueness must survive the rename.");
        });
    }

    [Test]
    public void IndexOnlyQuench_PrefixLengthIndex_IsIdempotent_AcrossRepeatedDeploys()
    {
        // Arrange - a prefix-length index (`code`(5)) already exists exactly as declared, and is
        // owned by the product. Regression coverage for a bug where the declared side and the
        // catalog-snapshot side normalized index columns differently, so a prefix index could never
        // compare equal to itself and was dropped + recreated on every single deploy.
        using var command = _connection.CreateCommand();
        command.CommandText = $"CREATE INDEX `idx_prefix_stable` ON `{_testDb}`.`{_testTableName}` (`code`(5))";
        command.ExecuteNonQuery();

        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_prefix_stable')";
        command.ExecuteNonQuery();

        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_prefix_stable`"",
                    ""IndexColumns"": ""`code`(5)"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - first pass may legitimately do work. The SECOND pass must do nothing at all -- that
        // convergence is exactly what the prefix mismatch breaks.
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        command.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - no rebuild/rename status line on the re-deploy. These are the exact phrases the
        // non-WhatIf branches log (see STEP 1/2/4 in SchemaSmith_IndexOnlyQuench.sql); a modified
        // index logs "Drop and recreate index:", not a bare "Drop index:".
        command.CommandText = $@"
            SELECT COUNT(*) FROM SchemaSmith_StatusMessages
            WHERE SessionId = CONNECTION_ID()
              AND (Message LIKE '%Drop and recreate index%' OR Message LIKE '%Create index%' OR Message LIKE '%Rename index%')";
        var actionCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(actionCount, Is.EqualTo(0),
            "A prefix-length index that already matches the declared definition must be left alone on a re-deploy.");

        command.CommandText = $@"
            SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_prefix_stable'";
        var subPart = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(subPart, Is.EqualTo(5), "The prefix length must survive re-deploy.");
    }

    private int ServerVersionNum()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_ServerVersionNum()";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // MySQL 8.0.13 is where functional indexes arrive; below it the feature cannot be created at all, so
    // these bodies have nothing to assert beyond the gate itself reporting 0.
    private bool ServerSupportsFunctionalIndex() => Platform != Platform.MariaDb && ServerVersionNum() >= 800;

    [Test]
    public void IndexOnlyQuench_SupportsFunctionalIndex_GateMatchesPlatform()
    {
        // The functional/expression-index gate (MySQL 8.0.13+) has no MariaDB equivalent in this form --
        // MariaDB expresses a "functional index" as an index on a generated/virtual column, which
        // extraction already reports as a plain column. Asserting the predicate directly gives MariaDB a
        // real, non-empty assertion for this feature's absence rather than a suite that simply skips it.
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_SupportsFunctionalIndex()";
        var supports = Convert.ToInt32(command.ExecuteScalar());

        // MySQL only gained functional indexes in 8.0.13, so the expectation follows the SERVER, not
        // the platform: asserting 1 for "any MySQL" fails on a genuine 5.7 where 0 is the correct answer.
        var expected = Platform == Platform.MariaDb || !ServerSupportsFunctionalIndex() ? 0 : 1;
        Assert.That(supports, Is.EqualTo(expected),
            $"SchemaSmith_SupportsFunctionalIndex() must be {expected} on {Platform} "
            + $"(server version {ServerVersionNum()})");
    }

    [Test]
    public void IndexOnlyQuench_ShouldAddPurelyFunctionalIndex()
    {
        // MariaDB has no equivalent for a functional/expression key part (see the gate test above) and
        // rejects the ((expr)) CREATE INDEX syntax outright, so the create path is exercised on MySQL only.
        if (Platform == Platform.MariaDb || !ServerSupportsFunctionalIndex())
            Assert.Ignore("Functional/expression indexes need MySQL 8.0.13+; MariaDB has no equivalent form "
                          + "and an older MySQL cannot create one.");

        // Arrange - a purely functional index: before this task, extraction built IndexColumns from
        // COLUMN_NAME alone (NULL for a key part like this one), so this shape extracted schema-invalid.
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_func_lower`"",
                    ""IndexColumns"": ""(lower(`name`))"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - the key part landed as a functional one (NULL COLUMN_NAME, populated EXPRESSION).
        command.CommandText = $@"
            SELECT COLUMN_NAME, EXPRESSION FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_func_lower'";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "Functional index should have been created");
        Assert.Multiple(() =>
        {
            Assert.That(reader["COLUMN_NAME"], Is.EqualTo(DBNull.Value), "A functional key part has no column name.");
            Assert.That(Convert.ToString(reader["EXPRESSION"]), Does.Contain("name"),
                "The expression key part must be readable back from the catalog.");
        });
    }

    [Test]
    public void IndexOnlyQuench_ShouldAddCompositeIndex_MixingPlainColumnAndExpression()
    {
        if (Platform == Platform.MariaDb || !ServerSupportsFunctionalIndex())
            Assert.Ignore("Functional/expression indexes need MySQL 8.0.13+; MariaDB has no equivalent form "
                          + "and an older MySQL cannot create one.");

        // Arrange - a composite index mixing a plain column with an expression key part: the case that
        // regresses first if the declared-side normalizer ever mis-splits the two on their shared comma.
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_mixed`"",
                    ""IndexColumns"": ""`code`,(lower(`name`))"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - both key parts landed, in declared order.
        command.CommandText = $@"
            SELECT SEQ_IN_INDEX, COLUMN_NAME, EXPRESSION FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_mixed'
            ORDER BY SEQ_IN_INDEX";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "First key part should exist");
        Assert.That(Convert.ToString(reader["COLUMN_NAME"]), Is.EqualTo("code"),
            "The plain column must stay first, in declared order.");
        Assert.That(reader.Read(), Is.True, "Second key part should exist");
        Assert.That(reader["COLUMN_NAME"], Is.EqualTo(DBNull.Value),
            "The expression key part must follow, with no column name.");
        Assert.That(Convert.ToString(reader["EXPRESSION"]), Does.Contain("name"));
    }

    [Test]
    public void IndexOnlyQuench_FunctionalIndex_IsIdempotent_AcrossRepeatedDeploys()
    {
        // Regression coverage for the exact hazard this task closes: if the declared-side normalizer and
        // the catalog-snapshot builds don't agree byte-for-byte on how a functional key part renders, the
        // compare never converges and the index is dropped + recreated on every deploy.
        if (Platform == Platform.MariaDb || !ServerSupportsFunctionalIndex())
            Assert.Ignore("Functional/expression indexes need MySQL 8.0.13+; MariaDB has no equivalent form "
                          + "and an older MySQL cannot create one.");

        using var command = _connection.CreateCommand();
        command.CommandText = $"CREATE INDEX `idx_func_stable` ON `{_testDb}`.`{_testTableName}` ((lower(`name`)))";
        command.ExecuteNonQuery();

        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_func_stable')";
        command.ExecuteNonQuery();

        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_func_stable`"",
                    ""IndexColumns"": ""(lower(`name`))"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - first pass may legitimately do work. The SECOND pass must do nothing at all -- that
        // convergence is exactly what a four-site representation mismatch breaks.
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        command.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - no rebuild/rename status line on the re-deploy (same phrases the prefix-length
        // idempotency test above checks for).
        command.CommandText = $@"
            SELECT COUNT(*) FROM SchemaSmith_StatusMessages
            WHERE SessionId = CONNECTION_ID()
              AND (Message LIKE '%Drop and recreate index%' OR Message LIKE '%Create index%' OR Message LIKE '%Rename index%')";
        var actionCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(actionCount, Is.EqualTo(0),
            "A functional index that already matches the declared definition must be left alone on a re-deploy -- " +
            "a mismatch between the declared-side normalizer and the catalog snapshot would show up here as a spurious rebuild.");

        command.CommandText = $@"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_func_stable'";
        var columnName = command.ExecuteScalar();
        Assert.That(columnName, Is.EqualTo(DBNull.Value), "The functional key part must survive re-deploy.");
    }

    [Test]
    public void IndexOnlyQuench_ShouldAddMultiValuedIndex_OverJsonArrayColumn()
    {
        // A multi-valued index (MySQL 8.0.17+) is still just a functional key part -- NULL COLUMN_NAME,
        // populated EXPRESSION -- so it rides the same SchemaSmith_SupportsFunctionalIndex() gate as any
        // other functional index; there is no dedicated gate to check (see SchemaSmith_SupportsFunctionalIndex.sql).
        // MariaDB has no equivalent feature at all.
        // ServerVersionNum() is major*100+minor, so it cannot separate 8.0.17 from 8.0.13 -- but it does
        // separate both from a 5.7 that has neither, which is the distinction that matters here.
        if (Platform == Platform.MariaDb || !ServerSupportsFunctionalIndex())
            Assert.Ignore("Multi-valued indexes need MySQL 8.0.17+; MariaDB has no equivalent form and an "
                          + "older MySQL cannot create one.");

        using var command = _connection.CreateCommand();
        command.CommandText = $"ALTER TABLE `{_testDb}`.`{_testTableName}` ADD COLUMN attrs JSON";
        command.ExecuteNonQuery();

        // Arrange - authored with JSON_EXTRACT rather than the col->'$.path' shorthand: MySQL rewrites
        // -> to json_extract(...) when it stores the key part, so the shorthand never appears in the
        // catalog and would never converge on a re-deploy. Authoring with the canonical function-call
        // form (what extraction actually reports) is the form that round-trips.
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_multi_tags`"",
                    ""IndexColumns"": ""(cast(json_extract(`attrs`,'$.tags') as char(20) array))"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - the key part landed as a multi-valued functional one (NULL COLUMN_NAME, populated
        // EXPRESSION retaining the ARRAY cast).
        command.CommandText = $@"
            SELECT COLUMN_NAME, EXPRESSION FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_multi_tags'";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "Multi-valued index should have been created");
        Assert.Multiple(() =>
        {
            Assert.That(reader["COLUMN_NAME"], Is.EqualTo(DBNull.Value), "A multi-valued key part has no column name.");
            Assert.That(Convert.ToString(reader["EXPRESSION"]), Does.Contain("array").IgnoreCase,
                "The multi-valued expression key part must be readable back from the catalog, retaining its ARRAY cast.");
        });
    }

    [Test]
    public void IndexOnlyQuench_MultiValuedIndex_IsIdempotent_AcrossRepeatedDeploys()
    {
        // Regression coverage for the hazard specific to multi-valued indexes: every multi-valued key
        // part carries a mandatory JSON-path string literal (CAST(col->'$.path' AS ... ARRAY) has no
        // form without one), and MySQL prefixes that literal with a charset introducer (_utf8mb4'...')
        // when it stores EXPRESSION -- the same reformatting already stripped from CHECK_CLAUSE. Left
        // unstripped on the index side, the declared (introducer-free) and catalog (introducer-bearing)
        // forms would never match, and the index would be dropped + recreated on every single deploy.
        // ServerVersionNum() is major*100+minor, so it cannot separate 8.0.17 from 8.0.13 -- but it does
        // separate both from a 5.7 that has neither, which is the distinction that matters here.
        if (Platform == Platform.MariaDb || !ServerSupportsFunctionalIndex())
            Assert.Ignore("Multi-valued indexes need MySQL 8.0.17+; MariaDB has no equivalent form and an "
                          + "older MySQL cannot create one.");

        using var command = _connection.CreateCommand();
        command.CommandText = $"ALTER TABLE `{_testDb}`.`{_testTableName}` ADD COLUMN attrs JSON";
        command.ExecuteNonQuery();

        command.CommandText = $"CREATE INDEX `idx_multi_stable` ON `{_testDb}`.`{_testTableName}` ((cast(json_extract(`attrs`,'$.tags') as char(20) array)))";
        command.ExecuteNonQuery();

        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_multi_stable')";
        command.ExecuteNonQuery();

        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_multi_stable`"",
                    ""IndexColumns"": ""(cast(json_extract(`attrs`,'$.tags') as char(20) array))"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - first pass may legitimately do work. The SECOND pass must do nothing at all.
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        command.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - no rebuild/rename status line on the re-deploy.
        command.CommandText = $@"
            SELECT COUNT(*) FROM SchemaSmith_StatusMessages
            WHERE SessionId = CONNECTION_ID()
              AND (Message LIKE '%Drop and recreate index%' OR Message LIKE '%Create index%' OR Message LIKE '%Rename index%')";
        var actionCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(actionCount, Is.EqualTo(0),
            "A multi-valued index that already matches the declared definition must be left alone on a re-deploy -- " +
            "an unstripped charset-introducer mismatch between the declared text and the catalog snapshot would show up here as a spurious rebuild on every pass.");

        command.CommandText = $@"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_multi_stable'";
        var columnName = command.ExecuteScalar();
        Assert.That(columnName, Is.EqualTo(DBNull.Value), "The multi-valued key part must survive re-deploy.");
    }

    [Test]
    public void IndexOnlyQuench_SpatialIndex_IsIdempotent_AcrossRepeatedDeploys()
    {
        // Regression coverage for the hazard specific to SPATIAL indexes: MySQL/MariaDB report a phantom
        // SUB_PART (always 32, not a declared prefix length) for a spatial key part. If the catalog-snapshot
        // builds render that phantom value the same way they render a real prefix, the declared (bare
        // `pt`) and catalog (`pt`(32)) forms never match, and the index is dropped + recreated on every
        // single deploy -- on all four engines, since SPATIAL is not MySQL-8.0-only like the tests above.
        using var command = _connection.CreateCommand();
        command.CommandText = $"ALTER TABLE `{_testDb}`.`{_testTableName}` ADD COLUMN pt POINT NOT NULL";
        command.ExecuteNonQuery();

        command.CommandText = $"CREATE SPATIAL INDEX `idx_spatial_stable` ON `{_testDb}`.`{_testTableName}` (`pt`)";
        command.ExecuteNonQuery();

        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_spatial_stable')";
        command.ExecuteNonQuery();

        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_spatial_stable`"",
                    ""IndexColumns"": ""`pt`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""SPATIAL""
                }}
            ]
        }}]";
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - first pass may legitimately do work. The SECOND pass must do nothing at all -- that
        // convergence is exactly what an unguarded phantom SUB_PART breaks.
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        command.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        command.ExecuteNonQuery();

        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - no rebuild/rename status line on the re-deploy.
        command.CommandText = $@"
            SELECT COUNT(*) FROM SchemaSmith_StatusMessages
            WHERE SessionId = CONNECTION_ID()
              AND (Message LIKE '%Drop and recreate index%' OR Message LIKE '%Create index%' OR Message LIKE '%Rename index%')";
        var actionCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(actionCount, Is.EqualTo(0),
            "A spatial index that already matches the declared definition must be left alone on a re-deploy -- " +
            "an unguarded phantom SUB_PART in the catalog snapshot would show up here as a spurious rebuild on every pass.");

        command.CommandText = $@"
            SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_spatial_stable'";
        var subPart = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(subPart, Is.EqualTo(32),
            "The spatial index must survive re-deploy -- SUB_PART stays the engine's own phantom value (32), " +
            "which SchemaSmith must never treat as a declared prefix to reconcile.");
    }

    [Test]
    public void IndexOnlyQuench_ShouldModifyIndex_WhenColumnsChange()
    {
        // Arrange - Create an index on just 'code'
        using var command = _connection.CreateCommand();
        command.CommandText = $"CREATE INDEX `idx_composite` ON `{_testDb}`.`{_testTableName}` (`code`)";
        command.ExecuteNonQuery();

        // Register it in ProductOwnership
        command.CommandText = $@"
            INSERT INTO `{MainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES ('TestProduct', '', '{_testDb}', 'INDEX', '{_testTableName}.idx_composite')";
        command.ExecuteNonQuery();

        // Define table with same index name but different columns
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_composite`"",
                    ""IndexColumns"": ""`code`, `name`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - Index should now have 2 columns
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_composite'";
        var columnCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(columnCount, Is.EqualTo(2), "Index should now have 2 columns");
    }

    [Test]
    public void IndexOnlyQuench_WhatIfMode_ShouldNotMakeChanges()
    {
        // Arrange
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_whatif`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act - Call with WhatIf=1
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 1, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - Index should NOT exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
              AND TABLE_NAME = '{_testTableName}'
              AND INDEX_NAME = 'idx_whatif'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.EqualTo(0), "Index should NOT have been created in WhatIf mode");
    }

    [Test]
    public void IndexOnlyQuench_ShouldTrackIndexInProductOwnership()
    {
        // Arrange
        using var command = _connection.CreateCommand();
        var tableJson = $@"[{{
            ""Name"": ""`{_testTableName}`"",
            ""Indexes"": [
                {{
                    ""Name"": ""`idx_tracked`"",
                    ""IndexColumns"": ""`code`"",
                    ""Unique"": false,
                    ""PrimaryKey"": false,
                    ""IndexType"": ""BTREE""
                }}
            ]
        }}]";

        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{_testDb}', '{tableJson.Replace("'", "''")}')";
        command.ExecuteNonQuery();

        // Act
        command.CommandText = $"CALL SchemaSmith_IndexOnlyQuench('TestProduct', '{_testDb}', 0, 0, 1)";
        command.ExecuteNonQuery();

        // Assert - Index should be tracked in ProductOwnership
        command.CommandText = $@"
            SELECT COUNT(*) FROM `{MainDb}`.SchemaSmith_ProductOwnership
            WHERE ProductName = 'TestProduct'
              AND ObjectSchema = '{_testDb}'
              AND ObjectType = 'INDEX'
              AND ObjectName = '{_testTableName}.idx_tracked'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.EqualTo(1), "Index should be tracked in ProductOwnership");
    }

    // ---- Foreign keys and audit: an index-only deploy manages indexes and never foreign keys ----

    private string CreateScratchDb(IDbCommand cmd, string ddl)
    {
        var db = $"IdxOnlyFk_{Guid.NewGuid():N}"[..22];
        cmd.CommandText = $"CREATE DATABASE `{db}`; {ddl.Replace("{db}", db)}";
        cmd.ExecuteNonQuery();
        return db;
    }

    private void RunIndexOnly(IDbCommand cmd, string db, string json, bool whatIf = false)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CALL `{MainDb}`.SchemaSmith_ParseTableJson('{db}', '{json.Replace("'", "''")}'); " +
                          $"CALL `{MainDb}`.SchemaSmith_IndexOnlyQuench('IdxOnlyFkProduct', '{db}', {(whatIf ? 1 : 0)}, 1, 1);";
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    private string IndexAudit(IDbCommand cmd) => Scalar(cmd,
        $"SELECT GROUP_CONCAT(CONCAT(ObjectName, ' ', ActionType) ORDER BY ObjectName, ActionType SEPARATOR '; ') FROM `{MainDb}`.SchemaSmith_ChangeAudit " +
        "WHERE SessionId = CONNECTION_ID() AND ObjectType = 'index'") ?? "";

    private void DropScratchDb(IDbCommand cmd, string db)
    {
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`; DELETE FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID();";
        cmd.ExecuteNonQuery();
    }

    // MySQL needs an index for a foreign key's columns, so the index the key uses cannot be dropped on its own (1553)
    // while the deploy changes it. It is replaced in one ALTER; a new definition that no longer serves the key leaves it
    // as it is.
    [TestCase("ParentId,Id", "ParentId,Id", TestName = "IndexOnlyQuench_ChangingAnIndexAForeignKeyUses_Deploys")]
    [TestCase("Id,ParentId", "ParentId", TestName = "IndexOnlyQuench_ChangingAnIndexAForeignKeyUses_KeptWhenTheKeyStillNeedsIt")]
    public void IndexOnlyQuench_ChangingAnIndexAForeignKeyUses(string declaredColumns, string expectedColumns)
    {
        using var cmd = _connection.CreateCommand();
        var db = CreateScratchDb(cmd, """
            CREATE TABLE `{db}`.`Parent` (`Id` INT NOT NULL PRIMARY KEY);
            CREATE TABLE `{db}`.`Child` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NOT NULL,
              INDEX `IX_Child_ParentId` (`ParentId`),
              CONSTRAINT `FK_Child_Parent` FOREIGN KEY (`ParentId`) REFERENCES `{db}`.`Parent` (`Id`));
            """);
        try
        {
            Assert.DoesNotThrow(() => RunIndexOnly(cmd, db, $$"""
                [ { "Name": "Child", "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" },
                                                  { "Name": "IX_Child_ParentId", "IndexColumns": "{{declaredColumns}}" } ] } ]
                """), "changing an index a foreign key uses must not stop the deploy");

            Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS " +
                                    $"WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'Child' AND INDEX_NAME = 'IX_Child_ParentId'"),
                Is.EqualTo(expectedColumns));
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{db}' AND CONSTRAINT_NAME = 'FK_Child_Parent'"),
                Is.EqualTo("1"), "an index-only deploy never touches a foreign key");
        }
        finally
        {
            DropScratchDb(cmd, db);
        }
    }

    // A foreign key with no declared index gets one from the engine. The package does not declare it, so with
    // DropUnknownIndexes on it was dropped as unknown, and the engine refused (1553).
    [Test]
    public void IndexOnlyQuench_AnUnknownIndexAForeignKeyNeeds_IsKept()
    {
        using var cmd = _connection.CreateCommand();
        var db = CreateScratchDb(cmd, """
            CREATE TABLE `{db}`.`Parent` (`Id` INT NOT NULL PRIMARY KEY);
            CREATE TABLE `{db}`.`Child` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NOT NULL,
              CONSTRAINT `FK_Child_Parent` FOREIGN KEY (`ParentId`) REFERENCES `{db}`.`Parent` (`Id`));
            """);
        try
        {
            Assert.DoesNotThrow(() => RunIndexOnly(cmd, db,
                """[ { "Name": "Child", "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" } ] } ]"""));

            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'Child' AND COLUMN_NAME = 'ParentId'"),
                Is.EqualTo("1"), "the index the key needs stays");
        }
        finally
        {
            DropScratchDb(cmd, db);
        }
    }

    // Dropping an unknown unique index used to drop, first, every foreign key referencing its TABLE -- including keys on
    // another column -- and an index-only deploy never re-creates foreign keys. No key is dropped now; an index a key
    // references is kept, and the other unknown index still goes.
    [Test]
    public void IndexOnlyQuench_DroppingUnknownUniqueIndexes_NeverDropsAnotherTablesForeignKey()
    {
        using var cmd = _connection.CreateCommand();
        var db = CreateScratchDb(cmd, """
            CREATE TABLE `{db}`.`Parent` (`Id` INT NOT NULL PRIMARY KEY, `Code` INT NOT NULL, `Other` INT NOT NULL,
              UNIQUE KEY `UQ_Code` (`Code`), UNIQUE KEY `UQ_Other` (`Other`));
            CREATE TABLE `{db}`.`ByCode` (`Id` INT NOT NULL PRIMARY KEY, `ParentCode` INT NOT NULL,
              CONSTRAINT `FK_ByCode` FOREIGN KEY (`ParentCode`) REFERENCES `{db}`.`Parent` (`Code`));
            CREATE TABLE `{db}`.`ById` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NOT NULL,
              CONSTRAINT `FK_ById` FOREIGN KEY (`ParentId`) REFERENCES `{db}`.`Parent` (`Id`));
            """);
        try
        {
            Assert.DoesNotThrow(() => RunIndexOnly(cmd, db,
                """[ { "Name": "Parent", "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" } ] } ]"""));

            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(CONSTRAINT_NAME ORDER BY CONSTRAINT_NAME) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS " +
                                        $"WHERE TABLE_SCHEMA = '{db}' AND CONSTRAINT_TYPE = 'FOREIGN KEY'"), Is.EqualTo("FK_ByCode,FK_ById"));
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(DISTINCT INDEX_NAME ORDER BY INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS " +
                                        $"WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'Parent'"), Is.EqualTo("PRIMARY,UQ_Code"),
                    "the index a key references is kept; the unreferenced unknown index is dropped");
            });
        }
        finally
        {
            DropScratchDb(cmd, db);
        }
    }

    // The deployment summary and --WhatIf read index changes from the audit, which an index-only deploy did not write
    // for ordinary indexes.
    [TestCase(false, "T.idx_gone dropped; T.idx_new created; T.idx_old created; T.idx_old dropped", TestName = "IndexOnlyQuench_AuditsIndexChanges")]
    [TestCase(true, "T.idx_gone wouldDrop; T.idx_new wouldCreate; T.idx_old wouldCreate; T.idx_old wouldDrop", TestName = "IndexOnlyQuench_WhatIf_AuditsIndexChanges")]
    public void IndexOnlyQuench_AuditsIndexChanges(bool whatIf, string expected)
    {
        using var cmd = _connection.CreateCommand();
        var db = CreateScratchDb(cmd, """
            CREATE TABLE `{db}`.`T` (`Id` INT NOT NULL PRIMARY KEY, `A` INT, `B` INT,
              INDEX `idx_old` (`A`), INDEX `idx_gone` (`B`));
            """);
        try
        {
            cmd.CommandText = $"DELETE FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            RunIndexOnly(cmd, db, """
                [ { "Name": "T", "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "Id" },
                                              { "Name": "idx_old", "IndexColumns": "B" },
                                              { "Name": "idx_new", "IndexColumns": "A,B" } ] } ]
                """, whatIf);

            // An index the package does not declare is named as the server spells its table: `t` under lower_case_table_names=1.
            Assert.That(IndexAudit(cmd), Is.EqualTo(expected).IgnoreCase);
        }
        finally
        {
            DropScratchDb(cmd, db);
        }
    }
}
