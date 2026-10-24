// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_AddMissingItemsTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldAddMissingIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AddMyIndex'), 'IDX_NewIndex', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        // Make sure the index gets the ProductName extended property
        cmd.CommandText = @"
SELECT CONVERT(VARCHAR(50), x.[value]) AS [value]
  FROM fn_listextendedproperty(default, 'Schema', 'dbo', 'Table', 'AddMyIndex', 'Index', default) x
  WHERE objname COLLATE DATABASE_DEFAULT = 'IDX_NewIndex'
    AND x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'
";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(_productName));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingIndexForIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AddMyIndexIO'), 'IDX_NewIndex', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        // Make sure the index gets the ProductName extended property
        cmd.CommandText = @"
SELECT CONVERT(VARCHAR(50), x.[value]) AS [value]
  FROM fn_listextendedproperty(default, 'Schema', 'dbo', 'Table', 'AddMyIndexIO', 'Index', default) x
  WHERE objname COLLATE DATABASE_DEFAULT = 'IDX_NewIndex'
    AND x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'
";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(_productName));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyColumn'), 'NewColumn', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyColumn'), 'CollatedColumn', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyColumn'), 'DontApply', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping(COLUMN_DEFAULT) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_Name = 'AddMyDefault' AND COLUMN_NAME = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingColumnLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping([definition]) FROM sys.check_constraints ck WITH (NOLOCK) WHERE ck.[parent_object_id] = OBJECT_ID('dbo.AddMyColumnCheck')  AND COL_NAME(ck.parent_object_id, ck.parent_column_id) = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Id]<(10)"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.CHK_AddMyTableCheck_MyCheck') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.FK_AddMyFK_SelfRef') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingStatistics()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS (SELECT * FROM sys.stats si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.AddMyStatistics') AND si.[Name] = 'ST_NewStats') THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddMissingFullTextIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyFullTextIndex'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyFullTextIndex'), 'Column2', 'AllowsNull') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS (SELECT * FROM sys.fulltext_indexes fi WITH (NOLOCK) WHERE fi.[object_id] = OBJECT_ID('dbo.AddMyFullTextIndex')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldAddMissingXmlIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT [Name] FROM sys.xml_indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.AddXmlIndex') AND xml_index_type = 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("XI_Primary"));

        cmd.CommandText = "SELECT [Name] FROM sys.xml_indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.AddXmlIndex') AND xml_index_type > 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("XI_Secondary_Path"));

        conn.Close();
    }

    [Test]
    public void ShouldAddMissingClusteredColumnStoreIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT [Name] FROM sys.indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.AddClusteredlColumnStoreIndex') AND [type] = 5";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("cci_ColumnStore"));

        conn.Close();
    }


    [Test]
    public void ShouldAddMissingNonClusteredColumnStoreIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT [Name] FROM sys.indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.AddNonClusteredlColumnStoreIndex') AND [type] = 6";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("nci_ColumnStore"));

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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The variant whose ShouldApplyExpression evaluates true ("1=1") should survive
        // with its declared type (INT). The other variant (VARCHAR(50), gated on "0=1") is skipped.
        cmd.CommandText = @"
SELECT t.[name]
  FROM sys.columns c WITH (NOLOCK)
  JOIN sys.types t WITH (NOLOCK) ON t.user_type_id = c.user_type_id
 WHERE c.object_id = OBJECT_ID('dbo.AddMyVariantColumn')
   AND c.[name] = 'payload'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("int"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameIndexesHaveMutuallyExclusiveShouldApply()
    {
        // Same-name two-variant pattern for indexes -- the surviving variant should land with
        // its declared shape, the skipped variant should not appear.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The variant whose ShouldApply evaluates true is a non-clustered index on [Col1] alone.
        // The other variant (also named IDX_Variant) targets [Col2] and is gated false.
        cmd.CommandText = @"
SELECT STUFF((
    SELECT ',' + c.[name]
      FROM sys.index_columns ic
      JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
     WHERE ic.object_id = OBJECT_ID('dbo.AddMyVariantIndex')
       AND ic.index_id = (SELECT index_id FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.AddMyVariantIndex') AND name = 'IDX_Variant')
     ORDER BY ic.key_ordinal
     FOR XML PATH('')
), 1, 1, '')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col1"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameFKsHaveMutuallyExclusiveShouldApply()
    {
        // Same-name two-variant pattern for foreign keys -- the surviving variant should land.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The surviving variant references [Col1]; the skipped variant referenced [Col2].
        cmd.CommandText = @"
SELECT c.[name]
  FROM sys.foreign_keys fk
  JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
  JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
 WHERE fk.[name] = 'FK_AddMyVariantFK_Variant'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col1"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldKeepOneVariantWhenTwoSameNameCheckConstraintsHaveMutuallyExclusiveShouldApply()
    {
        // Same-name two-variant pattern for check constraints -- the surviving variant should land.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The surviving variant's expression checks [Col1]>0; the skipped variant checked [Col1]<0.
        cmd.CommandText = @"
SELECT SchemaSmith.fn_StripParenWrapping([definition])
  FROM sys.check_constraints
 WHERE [name] = 'CHK_AddMyVariantCheck_Variant'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Col1]>(0)"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldEchoVariantNameInOperationMessages()
    {
        var messages = new System.Collections.Generic.List<string>();
        using var conn = (Microsoft.Data.SqlClient.SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (Microsoft.Data.SqlClient.SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        const string json = @"[{
            ""Schema"": ""[dbo]"",
            ""Name"": ""[VariantLogTest]"",
            ""CompressionType"": ""NONE"",
            ""Columns"": [ { ""Name"": ""[Id]"", ""DataType"": ""INT"", ""Nullable"": false } ],
            ""Indexes"": [
                { ""Name"": ""[IX_VariantLog]"", ""IndexColumns"": ""[Id]"", ""ShouldApplyExpression"": ""1=1"", ""VariantName"": ""Modern engines"" },
                { ""Name"": ""[IX_VariantLog_Legacy]"", ""IndexColumns"": ""[Id]"", ""Unique"": true, ""ShouldApplyExpression"": ""1=0"", ""VariantName"": ""Legacy engines"" }
            ]
        }]";
        try
        {
            RunTableQuenchProc(cmd, json);
            Assert.That(messages, Has.Some.Contains("(variant: Modern engines)"));
            Assert.That(messages, Has.None.Contains("Legacy engines"));
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.VariantLogTest";
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyMultipleItems'), 'Column1', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyMultipleItems'), 'Column2', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'AddMyMultipleItems' AND COLUMN_NAME = 'Column1'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("varchar"));
            Assert.That(reader.GetString(1), Is.EqualTo("YES"));
        }

        cmd.CommandText = "SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'AddMyMultipleItems' AND COLUMN_NAME = 'Column2'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("int"));
            Assert.That(reader.GetString(1), Is.EqualTo("YES"));
        }

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AddMyMultipleItems'), 'IDX_MultiColumn1', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AddMyMultipleItems'), 'IDX_MultiColumn2', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldBootstrapTableWithMultipleColumnsAndIndexesInOneQuench()
    {
        // Same fold path, but for a table that does not pre-exist: the table itself, its non-key
        // columns, and its indexes are all created together from a single JSON table definition.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.AddMyMultipleItemsBootstrap') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyMultipleItemsBootstrap'), 'Column1', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AddMyMultipleItemsBootstrap'), 'Column2', 'ColumnId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AddMyMultipleItemsBootstrap'), 'IDX_BootstrapColumn1', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AddMyMultipleItemsBootstrap'), 'IDX_BootstrapColumn2', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
--TableQuench_ShouldAddMissingIndex
CREATE TABLE dbo.AddMyIndex (Id INT NOT NULL)
--TableQuench_ShouldAddMissingColumn
CREATE TABLE dbo.AddMyColumn (Id INT NOT NULL)
--TableQuench_ShouldAddMissingDefault
CREATE TABLE dbo.AddMyDefault (Id INT NOT NULL)
--TableQuench_ShouldAddMissingColumnLevelCheckConstraint
CREATE TABLE dbo.AddMyColumnCheck (Id INT NOT NULL)
--TableQuench_ShouldAddMissingTableLevelCheckConstraint
CREATE TABLE dbo.AddMyTableCheck (Id INT NOT NULL, Col2 INT)
--TableQuench_ShouldAddMissingForeignKey
CREATE TABLE dbo.AddMyFK (Id INT NOT NULL PRIMARY KEY, Col2 INT)
--TableQuench_ShouldAddMissingStatistics
CREATE TABLE dbo.AddMyStatistics (Id INT NOT NULL)
--TableQuench_ShouldAddMissingFullTextIndex
CREATE TABLE dbo.AddMyFullTextIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX UDX_Key ON dbo.AddMyFullTextIndex ([Column1])
--ShouldAddMissingXmlIndex
CREATE TABLE dbo.AddXmlIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 XML NULL, CONSTRAINT PK_AddXmlIndex PRIMARY KEY CLUSTERED (Column1))
--ShouldAddMissingClusteredColumnStoreIndex
CREATE TABLE dbo.AddClusteredlColumnStoreIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 INT NULL, Column4 VARCHAR(100) NULL, Column5 INT NOT NULL)
--ShouldAddMissingNonClusteredColumnStoreIndex
CREATE TABLE dbo.AddNonClusteredlColumnStoreIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 INT NULL, Column4 VARCHAR(100) NULL, Column5 INT NOT NULL)
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameColumnsHaveMutuallyExclusiveShouldApply
CREATE TABLE dbo.AddMyVariantColumn (Id INT NOT NULL)
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameIndexesHaveMutuallyExclusiveShouldApply
CREATE TABLE dbo.AddMyVariantIndex (Id INT NOT NULL, Col1 INT NOT NULL, Col2 INT NOT NULL)
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameFKsHaveMutuallyExclusiveShouldApply
CREATE TABLE dbo.AddMyVariantFK (Id INT NOT NULL PRIMARY KEY, Col1 INT NULL, Col2 INT NULL)
--TableQuench_ShouldKeepOneVariantWhenTwoSameNameCheckConstraintsHaveMutuallyExclusiveShouldApply
CREATE TABLE dbo.AddMyVariantCheck (Id INT NOT NULL, Col1 INT NULL)
--TableQuench_ShouldAddMultipleColumnsAndIndexesToExistingTableInOneQuench
CREATE TABLE dbo.AddMyMultipleItems (Id INT NOT NULL)
--TableQuench_ShouldBootstrapTableWithMultipleColumnsAndIndexesInOneQuench (table created entirely by the quench)


--Index Only
--TableQuench_ShouldAddMissingIndexForIndexOnly
CREATE TABLE dbo.AddMyIndexIO (Id INT NOT NULL)
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[AddMyIndex]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_NewIndex]",
                      "IndexColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyColumn]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[NewColumn]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Sparse": true
                    },
                    {
                      "Name": "[CollatedColumn]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": "Latin1_General_CS_AS",
                      "DataMaskFunction": "default()"
                    },
                    {
                      "Name": "[DontApply]",
                      "DataType": "INT",
                      "Nullable": true,
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyDefault]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false,
                      "Default": "0"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyColumnCheck]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false,
                      "CheckExpression": "[Id]<10"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyTableCheck]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "Nullable": false,
                      "DataType": "INT"
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_AddMyTableCheck_MyCheck",
                      "Expression": "[Id]<[Col2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyFK]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_AddMyFK_SelfRef]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[AddMyFK]",
                      "RelatedColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyStatistics]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Statistics": [
                    {
                       "Name": "ST_NewStats",
                       "Columns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyFullTextIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    }
                ],
                "FullTextIndex": {
                    "FullTextCatalog": "FT_Catalog",
                    "KeyIndex": "UDX_Key",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddXmlIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "XML",
                      "Nullable": true
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[PK_AddXmlIndex]",
                      "IndexColumns": "[Column1]",
                      "Clustered": true,
                      "PrimaryKey": true,
                      "Unique": true
                    }
                ],
                "XmlIndexes": [
                    {
                      "Name": "[XI_Primary]",
                      "Column": "[Column3]",
                      "IsPrimary": true
                    },
                    {
                      "Name": "[XI_Secondary_Path]",
                      "Column": "[Column3]",
                      "IsPrimary": false,
                      "PrimaryIndex": "[XI_Primary]",
                      "SecondaryIndexType": "PATH"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddClusteredlColumnStoreIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column4]",
                      "DataType": "VARCHAR(100)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column5]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[cci_ColumnStore]",
                      "Clustered": true,
                      "ColumnStore": true,
                      "PrimaryKey": false,
                      "Unique": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddNonClusteredlColumnStoreIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column4]",
                      "DataType": "VARCHAR(100)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column5]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[nci_ColumnStore]",
                      "Clustered": false,
                      "ColumnStore": true,
                      "PrimaryKey": false,
                      "Unique": false,
                      "IncludeColumns": "[Column2],[Column3],[Column4]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyVariantColumn]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[payload]",
                      "DataType": "INT",
                      "Nullable": true,
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "[payload]",
                      "DataType": "VARCHAR(50)",
                      "Nullable": true,
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyVariantIndex]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Col1]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Col2]", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_Variant]",
                      "IndexColumns": "[Col1]",
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "[IDX_Variant]",
                      "IndexColumns": "[Col2]",
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyVariantFK]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Col1]", "DataType": "INT", "Nullable": true },
                    { "Name": "[Col2]", "DataType": "INT", "Nullable": true }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_AddMyVariantFK_Variant]",
                      "Columns": "[Col1]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[AddMyVariantFK]",
                      "RelatedColumns": "[Id]",
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "[FK_AddMyVariantFK_Variant]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[AddMyVariantFK]",
                      "RelatedColumns": "[Id]",
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyVariantCheck]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Col1]", "DataType": "INT", "Nullable": true }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_AddMyVariantCheck_Variant",
                      "Expression": "[Col1]>0",
                      "ShouldApplyExpression": "1=1"
                    },
                    {
                      "Name": "CHK_AddMyVariantCheck_Variant",
                      "Expression": "[Col1]<0",
                      "ShouldApplyExpression": "0=1"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyMultipleItems]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Column1]", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "[Column2]", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "[IDX_MultiColumn1]", "IndexColumns": "[Column1]" },
                    { "Name": "[IDX_MultiColumn2]", "IndexColumns": "[Column2]" }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddMyMultipleItemsBootstrap]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Column1]", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "[Column2]", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "[IDX_BootstrapColumn1]", "IndexColumns": "[Column1]" },
                    { "Name": "[IDX_BootstrapColumn2]", "IndexColumns": "[Column2]" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[AddMyIndexIO]",
                "Indexes": [
                    {
                      "Name": "[IDX_NewIndex]",
                      "IndexColumns": "[Id]"
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json, indexOnly: true);
        conn.Close();
    }
}
