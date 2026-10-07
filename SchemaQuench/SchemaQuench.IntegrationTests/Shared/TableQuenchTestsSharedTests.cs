// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.Utility;
using Schema.DataAccess;
using Schema.Domain;
using System;

using NUnit.Framework;
using SchemaQuench.IntegrationTests.MySQL;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for table quenching functionality.
/// Uses dynamically created test databases via FixtureSetup.
/// </summary>
[Category("Integration")]
public abstract class TableQuenchTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection;
    private string _testTablePrefix;
    private string _testDb;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        _testDb = MainDb;

        // Create unique prefix for test tables
        _testTablePrefix = $"_test_quench_{DateTime.Now:HHmmss}";

        // ForgeKindler is already deployed by FixtureSetup - no need to call it again
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up any test tables
        try
        {
            using var command = _connection.CreateCommand();
            var tablesToClean = new[]
            {
                "_new", "_modify", "_rename", "_old", "_gen", "_gen_dep",
                "_add_gen", "_ordinal", "_stored_gen", "_tbl_rename", "_tbl_renamed",
                "_col_rename", "_shouldapply", "_shouldapply_col", "_shouldapply_idx",
                "_sa_expr_f", "_sa_expr_t", "_sa_expr_col", "_sa_fk_parent", "_sa_fk_child"
            };
            foreach (var suffix in tablesToClean)
            {
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{_testTablePrefix}{suffix}`";
                command.ExecuteNonQuery();
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void TableQuenchProcedure_Exists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT ROUTINE_NAME FROM INFORMATION_SCHEMA.ROUTINES
            WHERE {MySqlNameMatch.FoldedRoutineSchema("ROUTINE_SCHEMA", $"'{MainDb}'")}
            AND ROUTINE_NAME = 'SchemaSmith_TableQuench'
            AND ROUTINE_TYPE = 'PROCEDURE'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_TableQuench"));
    }

    [Test]
    public void ParseTableJsonProcedure_Exists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT ROUTINE_NAME FROM INFORMATION_SCHEMA.ROUTINES
            WHERE {MySqlNameMatch.FoldedRoutineSchema("ROUTINE_SCHEMA", $"'{MainDb}'")}
            AND ROUTINE_NAME = 'SchemaSmith_ParseTableJson'
            AND ROUTINE_TYPE = 'PROCEDURE'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_ParseTableJson"));
    }

    [Test]
    public void MissingTableAndColumnQuenchProcedure_Exists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT ROUTINE_NAME FROM INFORMATION_SCHEMA.ROUTINES
            WHERE {MySqlNameMatch.FoldedRoutineSchema("ROUTINE_SCHEMA", $"'{MainDb}'")}
            AND ROUTINE_NAME = 'SchemaSmith_MissingTableAndColumnQuench'
            AND ROUTINE_TYPE = 'PROCEDURE'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_MissingTableAndColumnQuench"));
    }

    [Test]
    public void ModifiedTableQuenchProcedure_Exists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT ROUTINE_NAME FROM INFORMATION_SCHEMA.ROUTINES
            WHERE {MySqlNameMatch.FoldedRoutineSchema("ROUTINE_SCHEMA", $"'{MainDb}'")}
            AND ROUTINE_NAME = 'SchemaSmith_ModifiedTableQuench'
            AND ROUTINE_TYPE = 'PROCEDURE'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_ModifiedTableQuench"));
    }

    [Test]
    public void TableQuench_CanCreateNewTable()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_new";
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{
                        ""Name"": ""id"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false,
                        ""AutoIncrement"": true
                    }},
                    {{
                        ""Name"": ""name"",
                        ""DataType"": ""VARCHAR(100)"",
                        ""Nullable"": true
                    }}
                ],
                ""Indexes"": [
                    {{
                        ""Name"": ""PRIMARY"",
                        ""PrimaryKey"": true,
                        ""IndexColumns"": ""id""
                    }}
                ]
            }}
        ]";

        // Step 1: Parse JSON into temp tables
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Step 2: Create missing tables and columns
        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify table was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, $"Table {tableName} should have been created");
    }

    [Test]
    public void TableQuench_CanAddColumnToExistingTable()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_modify";

        // First create a table with one column
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(50) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // Now use the quench procedures to add a new column
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{
                        ""Name"": ""id"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false,
                        ""AutoIncrement"": true
                    }},
                    {{
                        ""Name"": ""name"",
                        ""DataType"": ""VARCHAR(50)"",
                        ""Nullable"": false
                    }},
                    {{
                        ""Name"": ""description"",
                        ""DataType"": ""TEXT"",
                        ""Nullable"": true
                    }}
                ],
                ""Indexes"": [
                    {{
                        ""Name"": ""PRIMARY"",
                        ""PrimaryKey"": true,
                        ""IndexColumns"": ""id""
                    }}
                ]
            }}
        ]";

        // Step 1: Parse JSON
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Step 2: Create missing tables and columns
        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify new column exists
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'description'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Column 'description' should have been added");
    }

    [Test]
    public void TableQuench_WhatIfMode_DoesNotModify()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_whatif";
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{
                        ""Name"": ""id"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false
                    }}
                ]
            }}
        ]";

        // Step 1: Parse JSON
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Step 2: Run with WhatIf = 1
        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 1);
        command.ExecuteNonQuery();

        // Verify table was NOT created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Null, $"Table {tableName} should NOT have been created in WhatIf mode");
    }

    [Test]
    public void SafeBacktickWrap_HandlesReservedWords()
    {
        using var command = _connection.CreateCommand();

        // Test with a MySQL reserved word
        command.CommandText = "SELECT SchemaSmith_SafeBacktickWrap('select')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`select`"));
    }

    [Test]
    public void StripBacktickWrapping_RemovesBackticks()
    {
        using var command = _connection.CreateCommand();

        command.CommandText = "SELECT SchemaSmith_StripBacktickWrapping('`my_table`')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("my_table"));
    }

    [Test]
    public void MissingIndexesAndConstraintsQuenchProcedure_Exists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT ROUTINE_NAME FROM INFORMATION_SCHEMA.ROUTINES
            WHERE {MySqlNameMatch.FoldedRoutineSchema("ROUTINE_SCHEMA", $"'{MainDb}'")}
            AND ROUTINE_NAME = 'SchemaSmith_MissingIndexesAndConstraintsQuench'
            AND ROUTINE_TYPE = 'PROCEDURE'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_MissingIndexesAndConstraintsQuench"));
    }

    [Test]
    public void IndexQuench_CanCreateRegularIndex()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_idx";

        // Create a table without index
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `email` VARCHAR(100) NOT NULL,
                `status` VARCHAR(20) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with index definition
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""email"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }},
                    {{ ""Name"": ""status"", ""DataType"": ""VARCHAR(20)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }},
                    {{ ""Name"": ""idx_email"", ""PrimaryKey"": false, ""Unique"": false, ""IndexColumns"": ""email"" }}
                ]
            }}
        ]";

        // Parse JSON and create indexes
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify index was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_email'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Index idx_email should have been created");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void IndexQuench_CanCreateUniqueIndex()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_uniq";

        // Create a table
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `code` VARCHAR(50) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with unique index
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""code"", ""DataType"": ""VARCHAR(50)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }},
                    {{ ""Name"": ""idx_code_unique"", ""PrimaryKey"": false, ""Unique"": true, ""IndexColumns"": ""code"" }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify unique index was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_code_unique'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Unique index should have been created");
        Assert.That(Convert.ToInt32(result), Is.EqualTo(0), "Index should be unique (NON_UNIQUE = 0)");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void ForeignKeyQuench_CanCreateForeignKey()
    {
        using var command = _connection.CreateCommand();

        var parentTable = $"{_testTablePrefix}_parent";
        var childTable = $"{_testTablePrefix}_child";

        // Create parent table
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{parentTable}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // Create child table without FK
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{childTable}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `parent_id` INT NOT NULL,
                INDEX `idx_parent` (`parent_id`)
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with foreign key
        var tableJson = $@"[
            {{
                ""Name"": ""{childTable}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""parent_id"", ""DataType"": ""INT"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }},
                    {{ ""Name"": ""idx_parent"", ""IndexColumns"": ""parent_id"" }}
                ],
                ""ForeignKeys"": [
                    {{
                        ""Name"": ""fk_child_parent"",
                        ""Columns"": ""parent_id"",
                        ""RelatedTable"": ""{parentTable}"",
                        ""RelatedColumns"": ""id"",
                        ""DeleteAction"": ""CASCADE"",
                        ""UpdateAction"": ""NO ACTION""
                    }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_ForeignKeyQuench(@productName, @databaseName, @whatIf, @dropUnknown, @dropRemovedFks)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.AddParameterWithValue("@dropRemovedFks", 1);
        command.ExecuteNonQuery();

        // Verify FK was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{childTable}'
            AND CONSTRAINT_NAME = 'fk_child_parent'
            AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Foreign key should have been created");

        // Cleanup (child first due to FK)
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{childTable}`";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{parentTable}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void CheckConstraintQuench_CanCreateCheckConstraint()
    {
        if (!TestVersionGates.SupportsCheckConstraints(Platform, MainConnectionString))
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_chk";

        // Create table without check constraint
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `age` INT NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with check constraint
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""age"", ""DataType"": ""INT"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ],
                ""CheckConstraints"": [
                    {{
                        ""Name"": ""chk_age_positive"",
                        ""Expression"": ""age >= 0""
                    }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify check constraint was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND CONSTRAINT_NAME = 'chk_age_positive'
            AND CONSTRAINT_TYPE = 'CHECK'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Check constraint should have been created");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void IndexQuench_WhatIfMode_DoesNotCreateIndex()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_whatif_idx";

        // Create table without index
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with index
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }},
                    {{ ""Name"": ""idx_name"", ""IndexColumns"": ""name"" }}
                ]
            }}
        ]";

        // Parse and run with WhatIf=1
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 1);
        command.ExecuteNonQuery();

        // Verify index was NOT created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_name'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Null, "Index should NOT have been created in WhatIf mode");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void DropUnknownIndexes_WhenEnabled_DropsUnmanagedIndex()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_dropidx";

        // Clean up any stale ProductOwnership entries from previous test runs
        command.CommandText = $@"DELETE FROM SchemaSmith_ProductOwnership
            WHERE ProductName = 'TestProduct' AND ObjectSchema = '{_testDb}'";
        command.ExecuteNonQuery();

        // Create table with an extra index
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL,
                `email` VARCHAR(100) NOT NULL,
                INDEX `idx_email` (`email`)
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // Register the idx_email as owned by our product (NOT PRIMARY - PRIMARY should stay)
        command.CommandText = @"INSERT INTO SchemaSmith_ProductOwnership
            (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
            VALUES (@productName, '', @objectSchema, 'INDEX', @objectName)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@objectSchema", _testDb);
        command.AddParameterWithValue("@objectName", $"{tableName}.idx_email");
        command.ExecuteNonQuery();

        // JSON without the idx_email index
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }},
                    {{ ""Name"": ""email"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and run with DropUnknownIndexes=1
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Index removal lives in ModifiedTableQuench, so THIS is the call that drops idx_email —
        // the trailing @dropUnknown, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct,
        // the two flags this test used to pass to MissingIndexesAndConstraintsQuench.
        // Non-index drop flags are all 0 so the call cannot remove tables or columns.
        command.CommandText = "CALL SchemaSmith_ModifiedTableQuench(@productName, @databaseName, @whatIf, 0, 0, 1, 1, 1, 0, @dropUnknown, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 1);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify index was dropped
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_email'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Null, "Index should have been dropped");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void DropUnknownIndexes_WhenDisabled_KeepsUnmanagedIndex()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_keepidx";

        // Clean up any stale ProductOwnership entries from previous test runs
        command.CommandText = $@"DELETE FROM SchemaSmith_ProductOwnership
            WHERE ProductName = 'TestProduct' AND ObjectSchema = '{_testDb}'";
        command.ExecuteNonQuery();

        // Create table with an extra index
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL,
                `email` VARCHAR(100) NOT NULL,
                INDEX `idx_email` (`email`)
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // idx_email is deliberately NOT registered in ProductOwnership — it is an out-of-band
        // (unmanaged) index. With DropUnknownIndexes=0 the out-of-band drop path is gated off, so it
        // must survive. (A product-OWNED index removed from the definition is a different axis:
        // it drops by default via DropIndexesRemovedFromProduct — see TableQuench_IndexParityTests.)

        // JSON without the idx_email index
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }},
                    {{ ""Name"": ""email"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and run with DropUnknownIndexes=0
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Index removal lives in ModifiedTableQuench, so THIS is the call that must run for the
        // assertion below to mean anything — the trailing @dropUnknown, 1 are DropUnknownIndexes
        // (off, the axis under test) and DropIndexesRemovedFromProduct (on, its default).
        // Non-index drop flags are all 0 so the call cannot remove tables or columns.
        command.CommandText = "CALL SchemaSmith_ModifiedTableQuench(@productName, @databaseName, @whatIf, 0, 0, 1, 1, 1, 0, @dropUnknown, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify index was NOT dropped
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_email'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Index should NOT have been dropped when DropUnknownIndexes=0");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void EngineChange_AltersTableEngine()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_engine";

        // Create table with MyISAM engine
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=MyISAM";
        command.ExecuteNonQuery();

        // JSON with InnoDB engine
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and modify
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Trailing 0, 0 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // lives in this procedure, and this test does not exercise it, so both stay off.
        command.CommandText = "CALL SchemaSmith_ModifiedTableQuench(@productName, @databaseName, @whatIf, @dropTables, @dropRemovedCols, 1, 1, 1, 0, 0, 0)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropTables", 0);
        command.AddParameterWithValue("@dropRemovedCols", 1);
        command.ExecuteNonQuery();

        // Verify engine was changed
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = command.ExecuteScalar();

        Assert.That(result?.ToString()?.ToUpper(), Is.EqualTo("INNODB"), "Engine should have been changed to InnoDB");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void CollationChange_AltersTableCollation()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_collation";

        // Create table with utf8mb4_general_ci collation
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci";
        command.ExecuteNonQuery();

        // JSON with utf8mb4_unicode_ci collation
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Collation"": ""utf8mb4_unicode_ci"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and modify
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Trailing 0, 0 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // lives in this procedure, and this test does not exercise it, so both stay off.
        command.CommandText = "CALL SchemaSmith_ModifiedTableQuench(@productName, @databaseName, @whatIf, @dropTables, @dropRemovedCols, 1, 1, 1, 0, 0, 0)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropTables", 0);
        command.AddParameterWithValue("@dropRemovedCols", 1);
        command.ExecuteNonQuery();

        // Verify collation was changed
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = command.ExecuteScalar();

        Assert.That(result?.ToString(), Is.EqualTo("utf8mb4_unicode_ci"), "Collation should have been changed to utf8mb4_unicode_ci");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void EngineChange_WhatIfMode_DoesNotChange()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_engine_whatif";

        // Create table with MyISAM engine
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=MyISAM";
        command.ExecuteNonQuery();

        // JSON with InnoDB engine
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and run in WhatIf mode
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Trailing 0, 0 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // lives in this procedure, and this test does not exercise it, so both stay off.
        command.CommandText = "CALL SchemaSmith_ModifiedTableQuench(@productName, @databaseName, @whatIf, @dropTables, @dropRemovedCols, 1, 1, 1, 0, 0, 0)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 1);
        command.AddParameterWithValue("@dropTables", 0);
        command.AddParameterWithValue("@dropRemovedCols", 1);
        command.ExecuteNonQuery();

        // Verify engine was NOT changed
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = command.ExecuteScalar();

        Assert.That(result?.ToString()?.ToUpper(), Is.EqualTo("MYISAM"), "Engine should NOT have been changed in WhatIf mode");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void TableQuench_CanCreateTableWithGeneratedColumn()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_gen";

        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{
                        ""Name"": ""id"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false,
                        ""AutoIncrement"": true
                    }},
                    {{
                        ""Name"": ""price"",
                        ""DataType"": ""DECIMAL(10,2)"",
                        ""Nullable"": false
                    }},
                    {{
                        ""Name"": ""quantity"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false
                    }},
                    {{
                        ""Name"": ""total"",
                        ""DataType"": ""DECIMAL(12,2)"",
                        ""Nullable"": true,
                        ""GenerationExpression"": ""price * quantity"",
                        ""Generated"": ""VIRTUAL""
                    }}
                ],
                ""Indexes"": [
                    {{
                        ""Name"": ""PRIMARY"",
                        ""PrimaryKey"": true,
                        ""IndexColumns"": ""id""
                    }}
                ]
            }}
        ]";

        // Run the full table quench pipeline — generated columns are deferred to MissingIndexesAndConstraintsQuench
        command.CommandText = "CALL SchemaSmith_TableQuench(@productName, @databaseName, @tableJson, @whatIf, @dropUnknown, @dropRemoved)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.AddParameterWithValue("@dropRemoved", 0);
        command.ExecuteNonQuery();

        // Verify generated column was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'total'";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.Not.Null, "Generated column 'total' should exist");
        Assert.That(result, Does.Contain("VIRTUAL GENERATED"), "Column should be a virtual generated column");

        // Verify the generated column works
        command.CommandText = $@"
            INSERT INTO `{_testDb}`.`{tableName}` (price, quantity) VALUES (10.50, 3);
            SELECT total FROM `{_testDb}`.`{tableName}` WHERE id = LAST_INSERT_ID()";
        var total = command.ExecuteScalar();
        Assert.That(Convert.ToDecimal(total), Is.EqualTo(31.50m), "Generated column should calculate correctly");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void TableQuench_GeneratedColumnDependencies_CreatesInCorrectOrder()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_gen_dep";

        // This tests that generated columns dependent on other generated columns
        // are created in the correct order (subtotal first, then total)
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{
                        ""Name"": ""id"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false,
                        ""AutoIncrement"": true
                    }},
                    {{
                        ""Name"": ""price"",
                        ""DataType"": ""DECIMAL(10,2)"",
                        ""Nullable"": false
                    }},
                    {{
                        ""Name"": ""quantity"",
                        ""DataType"": ""INT"",
                        ""Nullable"": false
                    }},
                    {{
                        ""Name"": ""total"",
                        ""DataType"": ""DECIMAL(12,2)"",
                        ""Nullable"": true,
                        ""GenerationExpression"": ""subtotal * 1.1"",
                        ""Generated"": ""VIRTUAL""
                    }},
                    {{
                        ""Name"": ""subtotal"",
                        ""DataType"": ""DECIMAL(12,2)"",
                        ""Nullable"": true,
                        ""GenerationExpression"": ""price * quantity"",
                        ""Generated"": ""VIRTUAL""
                    }}
                ],
                ""Indexes"": [
                    {{
                        ""Name"": ""PRIMARY"",
                        ""PrimaryKey"": true,
                        ""IndexColumns"": ""id""
                    }}
                ]
            }}
        ]";

        // Run the full table quench pipeline — generated columns are deferred to MissingIndexesAndConstraintsQuench
        command.CommandText = "CALL SchemaSmith_TableQuench(@productName, @databaseName, @tableJson, @whatIf, @dropUnknown, @dropRemoved)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.AddParameterWithValue("@dropRemoved", 0);
        command.ExecuteNonQuery();

        // Verify both generated columns exist
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND EXTRA LIKE '%GENERATED%'";
        var genColCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(genColCount, Is.EqualTo(2), "Both generated columns should exist");

        // Verify the calculations work
        command.CommandText = $@"
            INSERT INTO `{_testDb}`.`{tableName}` (price, quantity) VALUES (100.00, 2);
            SELECT subtotal, total FROM `{_testDb}`.`{tableName}` WHERE id = LAST_INSERT_ID()";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(Convert.ToDecimal(reader["subtotal"]), Is.EqualTo(200.00m), "Subtotal should be 200.00");
        Assert.That(Convert.ToDecimal(reader["total"]), Is.EqualTo(220.00m), "Total should be 220.00 (200 * 1.1)");
        reader.Close();

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void TableQuench_CanAddGeneratedColumnToExistingTable()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_add_gen";

        // First create a table without generated column
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `first_name` VARCHAR(50) NOT NULL,
                `last_name` VARCHAR(50) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // Now use quench to add a generated column
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""first_name"", ""DataType"": ""VARCHAR(50)"", ""Nullable"": false }},
                    {{ ""Name"": ""last_name"", ""DataType"": ""VARCHAR(50)"", ""Nullable"": false }},
                    {{
                        ""Name"": ""full_name"",
                        ""DataType"": ""VARCHAR(101)"",
                        ""Nullable"": true,
                        ""GenerationExpression"": ""CONCAT(first_name, ' ', last_name)"",
                        ""Generated"": ""VIRTUAL""
                    }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Run the full table quench pipeline — generated columns are deferred to MissingIndexesAndConstraintsQuench
        command.CommandText = "CALL SchemaSmith_TableQuench(@productName, @databaseName, @tableJson, @whatIf, @dropUnknown, @dropRemoved)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.AddParameterWithValue("@dropRemoved", 0);
        command.ExecuteNonQuery();

        // Verify generated column was added
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'full_name'";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.Not.Null, "Generated column 'full_name' should exist");
        Assert.That(result, Does.Contain("VIRTUAL GENERATED"));

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void TableQuench_ColumnsOrderedByOrdinalPosition()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_ordinal";

        // Create a table with columns in a specific order
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{ ""Name"": ""first_col"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""second_col"", ""DataType"": ""VARCHAR(50)"", ""Nullable"": true }},
                    {{ ""Name"": ""third_col"", ""DataType"": ""DATE"", ""Nullable"": true }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify columns are in the correct order
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION SEPARATOR ',')
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("first_col,second_col,third_col"),
            "Columns should be in ordinal position order, not alphabetical");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void TableQuench_StoredGeneratedColumn_CreatedCorrectly()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_stored_gen";

        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""value"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{
                        ""Name"": ""doubled"",
                        ""DataType"": ""INT"",
                        ""Nullable"": true,
                        ""GenerationExpression"": ""value * 2"",
                        ""Generated"": ""STORED""
                    }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Run the full table quench pipeline — generated columns are deferred to MissingIndexesAndConstraintsQuench
        command.CommandText = "CALL SchemaSmith_TableQuench(@productName, @databaseName, @tableJson, @whatIf, @dropUnknown, @dropRemoved)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.AddParameterWithValue("@dropRemoved", 0);
        command.ExecuteNonQuery();

        // Verify stored generated column
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'doubled'";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Does.Contain("STORED GENERATED"), "Column should be a stored generated column");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void TableRename_RenamesTableFromOldName()
    {
        using var command = _connection.CreateCommand();

        var oldTableName = $"{_testTablePrefix}_tbl_rename";
        var newTableName = $"{_testTablePrefix}_tbl_renamed";

        // Create table with old name
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{oldTableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with new name and OldName pointing to old table
        var tableJson = $@"[
            {{
                ""Name"": ""{newTableName}"",
                ""OldName"": ""{oldTableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and modify
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Table renames now run in MissingTableAndColumnQuench (before add-columns), so drive the
        // rename through that proc rather than ModifiedTableQuench.
        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify old table doesn't exist
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{oldTableName}'";
        var oldResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(oldResult, Is.EqualTo(0), "Old table should not exist after rename");

        // Verify new table exists
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{newTableName}'";
        var newResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(newResult, Is.EqualTo(1), "New table should exist after rename");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{newTableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void ColumnRename_RenamesColumnFromOldName()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_col_rename";

        // Create table with old column name
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `old_name` VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with new column name and OldName
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""new_name"", ""OldName"": ""old_name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }}
                ]
            }}
        ]";

        // Parse and modify
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        // Column renames now run in MissingTableAndColumnQuench (before add-columns), so drive the
        // rename through that proc rather than ModifiedTableQuench.
        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify old column doesn't exist
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'old_name'";
        var oldResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(oldResult, Is.EqualTo(0), "Old column should not exist after rename");

        // Verify new column exists
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'new_name'";
        var newResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(newResult, Is.EqualTo(1), "New column should exist after rename");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void ShouldApplyExpression_False_SkipsTableCreation()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_shouldapply";

        // JSON with ShouldApplyExpression = "0" (false)
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""ShouldApplyExpression"": ""0"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify table was NOT created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = Convert.ToInt32(command.ExecuteScalar());

        Assert.That(result, Is.EqualTo(0), "Table should NOT be created when ShouldApplyExpression is false");
    }

    [Test]
    public void ShouldApplyExpression_SelectForm_AppliesWhenTrue_SkipsWhenFalse()
    {
        // #282: a component gate may be written in the folder-gate form (a projection-only SELECT)
        // as well as a bare predicate. SchemaSmith_StripLeadingSelect strips a leading SELECT before
        // the predicate is embedded, so either form works consistently across all three engines.
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_selectform";

        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""ShouldApplyExpression"": ""SELECT 1 = 1"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""col_yes"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": true, ""ShouldApplyExpression"": ""SELECT 1 = 1"" }},
                    {{ ""Name"": ""col_no"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": true, ""ShouldApplyExpression"": ""SELECT 1 = 0"" }}
                ]
            }}
        ]";

        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{tableName}'";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "Table with a true SELECT-form gate should be created.");

        command.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{tableName}' AND COLUMN_NAME = 'col_yes'";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "Column with a true SELECT-form gate should be created.");

        command.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{tableName}' AND COLUMN_NAME = 'col_no'";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "Column with a false SELECT-form gate should be skipped.");
    }

    [Test]
    public void ShouldApplyExpression_FalseColumn_SkipsColumnCreation()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_shouldapply_col";

        // JSON with one column having ShouldApplyExpression = "false"
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""always_included"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }},
                    {{ ""Name"": ""conditionally_excluded"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": true, ""ShouldApplyExpression"": ""false"" }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify included column exists
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'always_included'";
        var includedResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(includedResult, Is.EqualTo(1), "Included column should exist");

        // Verify excluded column does NOT exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'conditionally_excluded'";
        var excludedResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(excludedResult, Is.EqualTo(0), "Column with ShouldApplyExpression=false should NOT be created");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void ShouldApplyExpression_FalseIndex_SkipsIndexCreation()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_shouldapply_idx";

        // JSON with one index having ShouldApplyExpression = "0"
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }},
                    {{ ""Name"": ""email"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""idx_name"", ""IndexColumns"": ""name"" }},
                    {{ ""Name"": ""idx_email"", ""IndexColumns"": ""email"", ""ShouldApplyExpression"": ""0"" }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify included index exists
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_name'";
        var includedResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(includedResult, Is.GreaterThan(0), "Included index should exist");

        // Verify excluded index does NOT exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND INDEX_NAME = 'idx_email'";
        var excludedResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(excludedResult, Is.EqualTo(0), "Index with ShouldApplyExpression=false should NOT be created");

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void ShouldApplyExpression_SqlExpression_FalseSkipsTable()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_sa_expr_f";

        // JSON with ShouldApplyExpression = "0=1" (evaluates to false via dynamic SQL)
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""ShouldApplyExpression"": ""0=1"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify table was NOT created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = Convert.ToInt32(command.ExecuteScalar());

        Assert.That(result, Is.EqualTo(0), "Table should NOT be created when ShouldApplyExpression evaluates to false (0=1)");
    }

    [Test]
    public void ShouldApplyExpression_SqlExpression_TrueAppliesTable()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_sa_expr_t";

        // JSON with ShouldApplyExpression = "1=1" (evaluates to true via dynamic SQL)
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""ShouldApplyExpression"": ""1=1"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""name"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify table WAS created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'";
        var result = Convert.ToInt32(command.ExecuteScalar());

        Assert.That(result, Is.EqualTo(1), "Table should be created when ShouldApplyExpression evaluates to true (1=1)");
    }

    [Test]
    public void ShouldApplyExpression_StringComparison_FalseSkipsColumn()
    {
        using var command = _connection.CreateCommand();

        var tableName = $"{_testTablePrefix}_sa_expr_col";

        // JSON with one column having ShouldApplyExpression = "'no' = 'yes'" (evaluates to false)
        // Simulates a token-resolved mismatch like "'{{IncludeAudit}}' = 'true'" becoming "'false' = 'true'"
        var tableJson = $@"[
            {{
                ""Name"": ""{tableName}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false }},
                    {{ ""Name"": ""always_here"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": false }},
                    {{ ""Name"": ""conditional_col"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": true, ""ShouldApplyExpression"": ""'no' = 'yes'"" }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingTableAndColumnQuench(@databaseName, @whatIf)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        // Verify always_here column exists
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'always_here'";
        var includedResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(includedResult, Is.EqualTo(1), "Unconditional column should exist");

        // Verify conditional_col does NOT exist
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{tableName}'
            AND COLUMN_NAME = 'conditional_col'";
        var excludedResult = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(excludedResult, Is.EqualTo(0), "Column with false string comparison ShouldApplyExpression should NOT be created");
    }

    [Test]
    public void ShouldApplyExpression_StringComparison_TrueAppliesFK()
    {
        using var command = _connection.CreateCommand();

        var parentTable = $"{_testTablePrefix}_sa_fk_parent";
        var childTable = $"{_testTablePrefix}_sa_fk_child";

        // Create parent table
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{parentTable}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `name` VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // Create child table without FK
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{childTable}` (
                `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `parent_id` INT NOT NULL,
                INDEX `idx_parent` (`parent_id`)
            ) ENGINE=InnoDB";
        command.ExecuteNonQuery();

        // JSON with foreign key having ShouldApplyExpression = "'yes' = 'yes'" (evaluates to true)
        // Simulates a token-resolved match like "'{{IncludeFK}}' = 'true'" becoming "'true' = 'true'"
        var tableJson = $@"[
            {{
                ""Name"": ""{childTable}"",
                ""Columns"": [
                    {{ ""Name"": ""id"", ""DataType"": ""INT"", ""Nullable"": false, ""AutoIncrement"": true }},
                    {{ ""Name"": ""parent_id"", ""DataType"": ""INT"", ""Nullable"": false }}
                ],
                ""Indexes"": [
                    {{ ""Name"": ""PRIMARY"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" }},
                    {{ ""Name"": ""idx_parent"", ""IndexColumns"": ""parent_id"" }}
                ],
                ""ForeignKeys"": [
                    {{
                        ""Name"": ""fk_sa_child_parent"",
                        ""Columns"": ""parent_id"",
                        ""RelatedTable"": ""{parentTable}"",
                        ""RelatedColumns"": ""id"",
                        ""DeleteAction"": ""CASCADE"",
                        ""UpdateAction"": ""NO ACTION"",
                        ""ShouldApplyExpression"": ""'yes' = 'yes'""
                    }}
                ]
            }}
        ]";

        // Parse and create
        command.CommandText = "CALL SchemaSmith_ParseTableJson(@databaseName, @tableJson)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@tableJson", tableJson);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_MissingIndexesAndConstraintsQuench(@productName, @databaseName, @whatIf, 1)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.ExecuteNonQuery();

        command.CommandText = "CALL SchemaSmith_ForeignKeyQuench(@productName, @databaseName, @whatIf, @dropUnknown, @dropRemovedFks)";
        command.Parameters.Clear();
        command.AddParameterWithValue("@productName", "TestProduct");
        command.AddParameterWithValue("@databaseName", _testDb);
        command.AddParameterWithValue("@whatIf", 0);
        command.AddParameterWithValue("@dropUnknown", 0);
        command.AddParameterWithValue("@dropRemovedFks", 1);
        command.ExecuteNonQuery();

        // Verify FK was created
        command.Parameters.Clear();
        command.CommandText = $@"
            SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{_testDb}'
            AND TABLE_NAME = '{childTable}'
            AND CONSTRAINT_NAME = 'fk_sa_child_parent'
            AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null, "Foreign key should be created when ShouldApplyExpression evaluates to true ('yes' = 'yes')");

        // Cleanup (child first due to FK)
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{childTable}`";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{parentTable}`";
        command.ExecuteNonQuery();
    }
}
