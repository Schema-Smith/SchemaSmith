// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_AddMissingItemsTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldAddMissingIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AddMissingItemsTests' AND tablename = 'AddMyIndex' AND indexname = 'IDX_NewIndex')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        // Make sure the index ownership is recorded
        cmd.CommandText = @"SELECT ""ProductName"" FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""Schema"" = 'AddMissingItemsTests' AND ""TableName"" = 'AddMyIndex' AND ""IndexName"" = 'IDX_NewIndex';";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(_productName));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingIndexForIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AddMissingItemsTests' AND tablename = 'AddMyIndexIO' AND indexname = 'IDX_NewIndexIO')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        // Make sure the index ownership is recorded
        cmd.CommandText = @"SELECT ""ProductName"" FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""Schema"" = 'AddMissingItemsTests' AND ""TableName"" = 'AddMyIndexIO' AND ""IndexName"" = 'IDX_NewIndexIO';";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(_productName));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyColumn' AND column_name = 'NewColumn')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyColumn' AND column_name = 'CollatedColumn')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyColumn' AND column_name = 'DontApply')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT \"SchemaSmith\".\"StripParenWrapping\"(column_default) FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyDefault' AND column_name = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'CHK_AddMyTableCheck_MyCheck' AND contype = 'c')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_AddMyFK_SelfRef' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingStatistics()
    {
        if (PgServerMajor() < 14) Assert.Ignore("Expression statistics (CREATE STATISTICS on an expression) require PostgreSQL 14+.");
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT stxname FROM pg_statistic_ext WHERE stxrelid = ('\"AddMissingItemsTests\".\"AddMyStatistics\"')::regclass AND stxname = 'ST_NewStats'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ST_NewStats"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameColumnsHaveMutuallyExclusiveShouldApply()
    {
        // Regression test for the silent-divergence bug surfaced 2026-06-01: when a table JSON
        // contained two same-named column entries with mutually exclusive ShouldApplyExpression,
        // both rows were silently dropped during JSON parsing and the column never landed. The
        // generated per-row DELETE statements matched on column name only, so any one row whose
        // expression evaluated false would delete every row that shared the name -- including
        // the sibling that was supposed to survive.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The variant whose ShouldApplyExpression evaluates true ("1=1") should survive
        // with its declared type (integer). The other variant (VARCHAR(50), gated on "0=1") is skipped.
        cmd.CommandText = @"
SELECT data_type
  FROM information_schema.columns
 WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyVariantColumn' AND column_name = 'payload'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("integer"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameIndexesHaveMutuallyExclusiveShouldApply()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The surviving variant is on (col1); the skipped variant targeted (col2).
        cmd.CommandText = @"
SELECT a.attname
  FROM pg_index i
  JOIN pg_class c ON c.oid = i.indexrelid
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
 WHERE n.nspname = 'AddMissingItemsTests' AND c.relname = 'IDX_Variant'
 ORDER BY a.attnum";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("col1"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameFKsHaveMutuallyExclusiveShouldApply()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The surviving variant references col1; the skipped variant referenced col2.
        cmd.CommandText = @"
SELECT a.attname
  FROM pg_constraint con
  JOIN pg_namespace n ON n.oid = con.connamespace
  JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY(con.conkey)
 WHERE n.nspname = 'AddMissingItemsTests' AND con.conname = 'FK_AddMyVariantFK_Variant'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("col1"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameCheckConstraintsHaveMutuallyExclusiveShouldApply()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The surviving variant's expression checks col1 > 0; the skipped variant checked col1 < 0.
        cmd.CommandText = @"
SELECT pg_get_constraintdef(con.oid, true)
  FROM pg_constraint con
  JOIN pg_namespace n ON n.oid = con.connamespace
 WHERE n.nspname = 'AddMissingItemsTests' AND con.conname = 'CHK_AddMyVariantCheck_Variant'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("col1 > 0"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldEchoVariantNameInOperationMessages()
    {
        var messages = new System.Collections.Generic.List<string>();
        using var conn = (Npgsql.NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE ""AddMissingItemsTests"".""VariantLogTest"" (""Id"" INT NOT NULL, ""col1"" INT NOT NULL, ""col2"" INT NOT NULL);";
        cmd.ExecuteNonQuery();
        var json = """
            [{
                "Schema": "AddMissingItemsTests",
                "Name": "VariantLogTest",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": false },
                    { "Name": "col2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_VariantLog", "IndexColumns": "col1", "ShouldApplyExpression": "1=1", "VariantName": "Modern engines" },
                    { "Name": "IDX_VariantLog", "IndexColumns": "col2", "ShouldApplyExpression": "0=1", "VariantName": "Legacy engines" }
                ]
            }]
            """;
        try
        {
            RunTableQuenchProc(cmd, json);
            Assert.That(messages, Has.Some.Contains("(variant: Modern engines)"));
            Assert.That(messages, Has.None.Contains("Legacy engines"));
        }
        finally
        {
            cmd.CommandText = @"DROP TABLE IF EXISTS ""AddMissingItemsTests"".""VariantLogTest""";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMultipleColumnsAndIndexesToExistingTableInOneQuench()
    {
        // A large convergence refactor folds multiple per-table ALTER/CREATE INDEX operations into
        // one batched statement (STRING_AGG). This exercises that fold path directly: 2+ new columns
        // AND 2+ new non-PK indexes land on the SAME table in a SINGLE quench run.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyMultipleItems' AND column_name = 'Column1' AND data_type = 'character varying' AND is_nullable = 'YES')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyMultipleItems' AND column_name = 'Column2' AND data_type = 'integer' AND is_nullable = 'YES')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AddMissingItemsTests' AND tablename = 'AddMyMultipleItems' AND indexname = 'IDX_MultiColumn1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AddMissingItemsTests' AND tablename = 'AddMyMultipleItems' AND indexname = 'IDX_MultiColumn2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldBootstrapTableWithMultipleColumnsAndIndexesInOneQuench()
    {
        // Same fold path, but for a table that does not pre-exist: the table itself, its non-key
        // columns, and its indexes are all created together from a single JSON table definition.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyMultipleItemsBootstrap')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyMultipleItemsBootstrap' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AddMissingItemsTests' AND table_name = 'AddMyMultipleItemsBootstrap' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AddMissingItemsTests' AND tablename = 'AddMyMultipleItemsBootstrap' AND indexname = 'IDX_BootstrapColumn1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AddMissingItemsTests' AND tablename = 'AddMyMultipleItemsBootstrap' AND indexname = 'IDX_BootstrapColumn2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE SCHEMA ""AddMissingItemsTests"";
--TableQuench_ShouldAddMissingIndex
CREATE TABLE ""AddMissingItemsTests"".""AddMyIndex"" (""Id"" INT NOT NULL);
--TableQuench_ShouldAddMissingColumn
CREATE TABLE ""AddMissingItemsTests"".""AddMyColumn"" (""Id"" INT NOT NULL);
--TableQuench_ShouldAddMissingDefault
CREATE TABLE ""AddMissingItemsTests"".""AddMyDefault"" (""Id"" INT NOT NULL);
--TableQuench_ShouldAddMissingTableLevelCheckConstraint
CREATE TABLE ""AddMissingItemsTests"".""AddMyTableCheck"" (""Id"" INT NOT NULL, ""Col2"" INT);
--TableQuench_ShouldAddMissingForeignKey
CREATE TABLE ""AddMissingItemsTests"".""AddMyFK"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT);
--TableQuench_ShouldAddMissingStatistics
CREATE TABLE ""AddMissingItemsTests"".""AddMyStatistics"" (""Id"" INT NOT NULL);
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameColumnsHaveMutuallyExclusiveShouldApply
CREATE TABLE ""AddMissingItemsTests"".""AddMyVariantColumn"" (""Id"" INT NOT NULL);
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameIndexesHaveMutuallyExclusiveShouldApply
CREATE TABLE ""AddMissingItemsTests"".""AddMyVariantIndex"" (""Id"" INT NOT NULL, ""col1"" INT NOT NULL, ""col2"" INT NOT NULL);
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameFKsHaveMutuallyExclusiveShouldApply
CREATE TABLE ""AddMissingItemsTests"".""AddMyVariantFK"" (""Id"" INT NOT NULL PRIMARY KEY, ""col1"" INT, ""col2"" INT);
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameCheckConstraintsHaveMutuallyExclusiveShouldApply
CREATE TABLE ""AddMissingItemsTests"".""AddMyVariantCheck"" (""Id"" INT NOT NULL, ""col1"" INT);
--TableQuench_ShouldAddMultipleColumnsAndIndexesToExistingTableInOneQuench
CREATE TABLE ""AddMissingItemsTests"".""AddMyMultipleItems"" (""Id"" INT NOT NULL);
--TableQuench_ShouldBootstrapTableWithMultipleColumnsAndIndexesInOneQuench (table created entirely by the quench)


--Index Only
--TableQuench_ShouldAddMissingIndexForIndexOnly
CREATE TABLE ""AddMissingItemsTests"".""AddMyIndexIO"" (""Id"" INT NOT NULL);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyIndex",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_NewIndex",
                      "IndexColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyColumn",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "NewColumn",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Sparse": true
                    },
                    {
                      "Name": "CollatedColumn",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": "az-Latn-x-icu"
                    },
                    {
                      "Name": "DontApply",
                      "DataType": "INT",
                      "Nullable": true,
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyDefault",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false,
                      "Default": "0"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyTableCheck",
                "Columns": [
                    {
                      "Name": "Id",
                      "Nullable": false,
                      "DataType": "INT"
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_AddMyTableCheck_MyCheck",
                      "Expression": "\"Id\"<\"Col2\""
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyFK",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_AddMyFK_SelfRef",
                      "Columns": "Col2",
                      "RelatedTableSchema": "AddMissingItemsTests",
                      "RelatedTable": "AddMyFK",
                      "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyStatistics",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Statistics": [
                    {
                       "Name": "ST_NewStats",
                       "StatisticsColumns": "(\"Id\" / 1)"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyVariantColumn",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "payload",
                      "DataType": "INT",
                      "Nullable": true,
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "payload",
                      "DataType": "VARCHAR(50)",
                      "Nullable": true,
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyVariantIndex",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": false },
                    { "Name": "col2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_Variant", "IndexColumns": "col1", "ShouldApplyExpression": "1=1" },
                    { "Name": "IDX_Variant", "IndexColumns": "col2", "ShouldApplyExpression": "0=1" }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyVariantFK",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": true },
                    { "Name": "col2", "DataType": "INT", "Nullable": true }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_AddMyVariantFK_Variant",
                      "Columns": "col1",
                      "RelatedTableSchema": "AddMissingItemsTests",
                      "RelatedTable": "AddMyVariantFK",
                      "RelatedColumns": "Id",
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "FK_AddMyVariantFK_Variant",
                      "Columns": "col2",
                      "RelatedTableSchema": "AddMissingItemsTests",
                      "RelatedTable": "AddMyVariantFK",
                      "RelatedColumns": "Id",
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyVariantCheck",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "col1", "DataType": "INT", "Nullable": true }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_AddMyVariantCheck_Variant",
                      "Expression": "col1 > 0",
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "CHK_AddMyVariantCheck_Variant",
                      "Expression": "col1 < 0",
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyMultipleItems",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Column1", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "Column2", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "IDX_MultiColumn1", "IndexColumns": "Column1" },
                    { "Name": "IDX_MultiColumn2", "IndexColumns": "Column2" }
                ]
            },
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyMultipleItemsBootstrap",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Column1", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "Column2", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "IDX_BootstrapColumn1", "IndexColumns": "Column1" },
                    { "Name": "IDX_BootstrapColumn2", "IndexColumns": "Column2" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);
        conn.Close();

        conn.Open();
        json = """
            [
            {
                "Schema": "AddMissingItemsTests",
                "Name": "AddMyIndexIO",
                "Indexes": [
                    {
                      "Name": "IDX_NewIndexIO",
                      "IndexColumns": "Id"
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json, indexOnly: true);
        conn.Close();
    }
}
