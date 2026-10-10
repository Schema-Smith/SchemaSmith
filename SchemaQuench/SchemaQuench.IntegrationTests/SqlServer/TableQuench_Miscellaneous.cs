// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using Microsoft.Data.SqlClient;
using System;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_Miscellaneous : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldRenameIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.RenameMyIndex'), 'IDX_RightName', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.RenameMyIndex'), 'IDX_WrongName', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRenameIndexIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.RenameMyIndexIO'), 'IDX_RightName', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.RenameMyIndexIO'), 'IDX_WrongName', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRenameUniqueConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.RenameMyUniqueConstraint'), 'UQ_NewName', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.RenameMyUniqueConstraint'), 'UQ_OldName', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.FK_DropFK_SelfRef') IS NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.FK_DropFK_SelfRef2') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldHandleRemovingConflictingCustomClusteredIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.DropConflictingClusteredIdx'), 'IDX_NewClusteredIdx', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.DropConflictingClusteredIdx'), 'IDX_Conflict', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void ShouldModifyTableCompression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') FROM sys.partitions AS p WITH (NOLOCK) WHERE p.[object_id] = OBJECT_ID('dbo.AlterTableCompression') AND p.index_id < 2";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PAGE"));
        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexCompression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') FROM sys.partitions AS p WITH (NOLOCK) WHERE p.[object_id] = OBJECT_ID('dbo.AlterIndexCompression') AND p.index_id >= 2";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PAGE"));
        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexCompressionIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') FROM sys.partitions AS p WITH (NOLOCK) WHERE p.[object_id] = OBJECT_ID('dbo.AlterIndexCompressionIO') AND p.index_id >= 2";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PAGE"));
        conn.Close();
    }

    [Test]
    public void ShouldModifyStatisticsWhenFilterExpressionChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping(filter_definition) FROM sys.stats si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.ModifyStatisticsFilterExpression') AND [Name] = 'ST_Filtered'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Column1]>(50)"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyStatisticsWhenColumnListChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.stats si WITH (NOLOCK) JOIN sys.stats_columns ic WITH (NOLOCK) ON ic.[object_id] = si.[object_id] AND ic.[stats_id] = si.[stats_id] WHERE si.[object_id] = OBJECT_ID('dbo.ModifyStatisticsColumnList') AND [Name] = 'ST_ColumnList'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldErrorWhenUpdatingWrongProductTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[TableOwnedByOtherProduct]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            }
            """;
        var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json));
        Assert.That(ex!.Message, Contains.Substring("One or more tables in this quench are already owned by another product"));

        conn.Close();
    }

    [Test]
    public void ShouldDropIndexNoLongerPartOfProduct()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.IndexNoLongerInProduct'), 'IDX_DropMe', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.IndexNoLongerInProduct'), 'IDX_Custom', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldDropXmlIndexNoLongerPartOfProduct()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.XmlIndexNoLongerInProduct'), 'XI_DropMe', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.XmlIndexNoLongerInProduct'), 'XI_KeepMe', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldDropIndexNoLongerPartOfProductIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.IndexNoLongerInProductIO'), 'IDX_DropMe', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        conn.Close();
    }

    [Test]
    public void ShouldDropTableNoLongerPartOfProduct()
    {
        var productName = Guid.NewGuid().ToString();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE TABLE dbo.TableNoLongerInProduct (Column1 INT NOT NULL)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'TableNoLongerInProduct'
";
        cmd.ExecuteNonQuery();

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[TableInProduct]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            }
            """;

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 1";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.TableNoLongerInProduct') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRemoveIdentityFromExistingColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Verify identity was removed
        Assert.That(GetColumnDataType(cmd, "RemoveIdentityFromColumn", "Column1"), Is.EqualTo("INT"));

        // Verify data was preserved
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.RemoveIdentityFromColumn";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2));

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
--TableQuench_ShouldRenameIndex
CREATE TABLE dbo.RenameMyIndex (Id INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_WrongName ON dbo.RenameMyIndex (Id)
--TableQuench_ShouldRenameUniqueConstraint
CREATE TABLE dbo.RenameMyUniqueConstraint (Id INT NOT NULL)
ALTER TABLE dbo.RenameMyUniqueConstraint ADD CONSTRAINT UQ_OldName UNIQUE (Id)
--TableQuench_ShouldHandleRemovingForeignKey
CREATE TABLE dbo.DropFK (Column1 INT NOT NULL, Column2 INT NOT NULL, CONSTRAINT PK_DropFK PRIMARY KEY (Column1))
CREATE UNIQUE INDEX UQ_Colum2 ON dbo.DropFK (Column2)
ALTER TABLE dbo.DropFK ADD CONSTRAINT FK_DropFK_SelfRef FOREIGN KEY (Column2) REFERENCES dbo.DropFK (Column1)
ALTER TABLE dbo.DropFK ADD CONSTRAINT FK_DropFK_SelfRef2 FOREIGN KEY (Column2) REFERENCES dbo.DropFK (Column1)
--ShouldHandleRemovingConflictingCustomClusteredIndex
CREATE TABLE dbo.DropConflictingClusteredIdx (Id INT NOT NULL)
CREATE CLUSTERED INDEX IDX_Conflict ON dbo.DropConflictingClusteredIdx (Id)
--ShouldModifyTableCompression
CREATE TABLE dbo.AlterTableCompression (Id INT NOT NULL) WITH (DATA_COMPRESSION=NONE)
--ShouldModifyIndexCompression
CREATE TABLE dbo.AlterIndexCompression (Id INT NOT NULL)
CREATE INDEX IDX_CompressionChange ON dbo.AlterIndexCompression (Id) WITH (DATA_COMPRESSION=NONE)
--ShouldModifyStatisticsWhenFilterExpressionChanges
CREATE TABLE dbo.ModifyStatisticsFilterExpression (Column1 INT NOT NULL)
CREATE STATISTICS ST_Filtered ON dbo.ModifyStatisticsFilterExpression ([Column1]) WHERE [Column1]>(20)
--ShouldModifyStatisticsWhenColumnListChanges
CREATE TABLE dbo.ModifyStatisticsColumnList (Column1 INT NOT NULL, Column2 INT NOT NULL)
CREATE STATISTICS ST_ColumnList ON dbo.ModifyStatisticsColumnList ([Column1])
--ShouldDropIndexNoLongerPartOfProduct
CREATE TABLE dbo.IndexNoLongerInProduct (Column1 INT NOT NULL)
CREATE INDEX IDX_DropMe ON IndexNoLongerInProduct (Column1)
CREATE INDEX IDX_Custom ON IndexNoLongerInProduct (Column1)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{_productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'IndexNoLongerInProduct'
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{_productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'IndexNoLongerInProduct', @level2type = N'Index', @level2name = 'IDX_DropMe'
--XmlIndexNoLongerInProduct
CREATE TABLE dbo.XmlIndexNoLongerInProduct (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 XML NULL, CONSTRAINT PK_XmlIndexNoLongerInProduct PRIMARY KEY CLUSTERED (Column1))
CREATE PRIMARY XML INDEX [XI_KeepMe] ON dbo.XmlIndexNoLongerInProduct (Column3)
CREATE XML INDEX [XI_DropMe] ON dbo.XmlIndexNoLongerInProduct (Column3) USING XML INDEX [XI_KeepMe] FOR PATH 
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{_productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'XmlIndexNoLongerInProduct'
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{_productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'XmlIndexNoLongerInProduct', @level2type = N'Index', @level2name = 'XI_KeepMe'
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{_productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'XmlIndexNoLongerInProduct', @level2type = N'Index', @level2name = 'XI_DropMe'

-- Index Only
--TableQuench_ShouldRenameIndexIndexOnly
CREATE TABLE dbo.RenameMyIndexIO (Id INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_WrongName ON dbo.RenameMyIndexIO (Id)
--ShouldModifyIndexCompressionIndexOnly
CREATE TABLE dbo.AlterIndexCompressionIO (Id INT NOT NULL)
CREATE INDEX IDX_CompressionChange ON dbo.AlterIndexCompressionIO (Id) WITH (DATA_COMPRESSION=NONE)
--ShouldDropIndexNoLongerPartOfProductIndexOnly
CREATE TABLE dbo.IndexNoLongerInProductIO (Column1 INT NOT NULL)
CREATE INDEX IDX_DropMe ON IndexNoLongerInProductIO (Column1)

-- Exception Cases
CREATE TABLE dbo.TableOwnedByOtherProduct (Column1 INT NOT NULL)
EXEC sp_addextendedproperty @name = N'ProductName', @value = 'OtherProduct', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'TableOwnedByOtherProduct'

--TableQuench_ShouldRemoveIdentityFromExistingColumn
CREATE TABLE dbo.RemoveIdentityFromColumn (Column1 INT IDENTITY(1,1) NOT NULL, Column2 VARCHAR(50) NOT NULL)
INSERT dbo.RemoveIdentityFromColumn (Column2) VALUES ('Row1'), ('Row2')
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[RenameMyIndex]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_RightName]",
                      "IndexColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[RenameMyUniqueConstraint]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[UQ_NewName]",
                      "IndexColumns": "[Id]",
                      "Unique": true,
                      "UniqueConstraint": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[DropFK]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_DropFK_SelfRef2]",
                      "Columns": "[Column2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[DropFK]",
                      "RelatedColumns": "[Column1]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[DropConflictingClusteredIdx]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_NewClusteredIdx]",
                      "IndexColumns": "[Id]",
                      "Clustered": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterTableCompression]",
                "CompressionType": "PAGE",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterIndexCompression]",
                "CompressionType": "PAGE",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_CompressionChange]",
                      "IndexColumns": "[Id]",
                      "CompressionType": "PAGE"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyStatisticsFilterExpression]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Statistics": [
                    {
                      "Name": "[ST_Filtered]",
                      "Columns": "[Column1]",
                      "FilterExpression": "[Column1]>(50)"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyStatisticsColumnList]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Statistics": [
                    {
                      "Name": "[ST_ColumnList]",
                      "Columns": "[Column2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[IndexNoLongerInProduct]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[XmlIndexNoLongerInProduct]",
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
                      "Name": "[PK_XmlIndexNoLongerInProduct]",
                      "IndexColumns": "[Column1]",
                      "Clustered": true,
                      "PrimaryKey": true,
                      "Unique": true
                    }
                ],
                "XmlIndexes": [
                    {
                      "Name": "[XI_KeepMe]",
                      "Column": "[Column3]",
                      "IsPrimary": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[RemoveIdentityFromColumn]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(50)",
                      "Nullable": false
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        // Index Only
        json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[RenameMyIndexIO]",
                "Indexes": [
                    {
                      "Name": "[IDX_RightName]",
                      "IndexColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterIndexCompressionIO]",
                "Indexes": [
                    {
                      "Name": "[IDX_CompressionChange]",
                      "IndexColumns": "[Id]",
                      "CompressionType": "PAGE"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[IndexNoLongerInProductIO]"
            }
            ]
            """;
        RunTableQuenchProc(cmd, json, indexOnly: true);


        conn.Close();
    }

    // JSON-ingest coverage at the OPENJSON cliff: compat 130 (SQL Server 2016) is where the JSON/OPENJSON
    // ingest path parses — at or above it SchemaSmith uses JSON, below it the XML encoding (the support
    // floor is now compat 100; the compat-100 XML path has its own coverage). Kindles a dedicated compat-130
    // database and deploys a package exercising the JSON ingest (OPENJSON) plus a computed column and index
    // (the FOR JSON / STRING_AGG convergence paths), proving the JSON path works end to end at the cliff —
    // not just that the pre-flight guard permits it. Empirically established 2026-07-27.
    [Test]
    public void ShouldDeployAgainstCompatibilityLevel130Database()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var compatDb = $"compat130_{uniqueId}";
        var productName = Guid.NewGuid().ToString();

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"CREATE DATABASE [{compatDb}]; ALTER DATABASE [{compatDb}] SET COMPATIBILITY_LEVEL = 130;";
            cmd.ExecuteNonQuery();

            conn.ChangeDatabase(compatDb);
            // Confirm the database really is at compat 130 (guards against the ALTER silently no-opping).
            cmd.CommandText = "SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(130), "Test database must be at compatibility level 130");

            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

            var json = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[c130_t]",
                "Columns": [
                    {"Name": "[id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[qty]", "DataType": "INT", "Nullable": false},
                    {"Name": "[doubled]", "DataType": "AS ([qty] * 2) PERSISTED"}
                ],
                "Indexes": [
                    {"Name": "[PK_c130_t]", "PrimaryKey": true, "IndexColumns": "[id]"},
                    {"Name": "[IX_c130_t_qty]", "IndexColumns": "[qty]"}
                ]
            }]
            """;
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = N'{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
            Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(), "Deploy against a compat-130 database should succeed");

            cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = 'c130_t'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "Table should be created at compat 130");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.computed_columns WHERE name = 'doubled'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "Computed column should be created at compat 130");
        }
        finally
        {
            conn.ChangeDatabase("master");
            // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            cmd.CommandText = $"IF DB_ID('{compatDb}') IS NOT NULL BEGIN ALTER DATABASE [{compatDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{compatDb}]; END";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    [Test]
    public void ShouldRenameTableViaOldName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var oldTableName = $"OldTableName_{uniqueId}";
        var newTableName = $"NewTableName_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE dbo.{oldTableName} (Id INT NOT NULL, Val INT NOT NULL)
INSERT INTO dbo.{oldTableName} (Id, Val) VALUES (1, 42)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{oldTableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{newTableName}}]",
    "OldName": "[{{oldTableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "INT", "Nullable": false}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"SELECT CAST(CASE WHEN OBJECT_ID('dbo.{oldTableName}') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "Old table should no longer exist");

        cmd.CommandText = $"SELECT CAST(CASE WHEN OBJECT_ID('dbo.{newTableName}') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "New table should exist");

        cmd.CommandText = $"SELECT Val FROM dbo.{newTableName} WHERE Id = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(42), "Data should be preserved after rename");

        conn.Close();
    }

    [Test]
    public void ShouldRenameColumnViaOldName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ColRenameTable_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, OldColName INT NOT NULL)
INSERT INTO dbo.{tableName} (Id, OldColName) VALUES (1, 99)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[NewColName]", "OldName": "[OldColName]", "DataType": "INT", "Nullable": false}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{tableName}' AND COLUMN_NAME = 'OldColName'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "Old column name should no longer exist");

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{tableName}' AND COLUMN_NAME = 'NewColName'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "New column name should exist");

        cmd.CommandText = $"SELECT NewColName FROM dbo.{tableName} WHERE Id = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(99), "Data should be preserved after column rename");

        conn.Close();
    }

    // Cross-engine parity guard for the PG 42P16 constraint-rename bug: rename a table via OldName
    // AND rename its own PK and FK. SQL Server reconciles this cleanly (object_id-keyed extended-
    // property ownership survives the rename), so this should pass on the reference engine.
    [Test]
    public void ShouldRenameTableAndItsConstraintsViaOldNameAcrossTwoDeploys()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var parentTable = $"CrCust_{uniqueId}";
        var oldTableName = $"CrOld_{uniqueId}";
        var newTableName = $"CrNew_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // v1 deploy: create + own the parent and the table (identity PK + FK) under the old name.
        var jsonV1 = $$"""
        [
            {
                "Schema": "[dbo]",
                "Name": "[{{parentTable}}]",
                "Columns": [{"Name": "[CustomerId]", "DataType": "INT", "Nullable": false}],
                "Indexes": [{"Name": "[PK_{{parentTable}}]", "PrimaryKey": true, "IndexColumns": "[CustomerId]"}]
            },
            {
                "Schema": "[dbo]",
                "Name": "[{{oldTableName}}]",
                "Columns": [
                    {"Name": "[OrderId]", "DataType": "INT IDENTITY(1,1)"},
                    {"Name": "[CustomerId]", "DataType": "INT"}
                ],
                "Indexes": [{"Name": "[PK_{{oldTableName}}]", "PrimaryKey": true, "IndexColumns": "[OrderId]"}],
                "ForeignKeys": [{"Name": "[FK_{{oldTableName}}_Customer]", "Columns": "[CustomerId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[{{parentTable}}]", "RelatedColumns": "[CustomerId]"}]
            }
        ]
        """;
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{jsonV1.Replace("'", "''")}', @DropTablesRemovedFromProduct = 1, @DropUnknownIndexes = 0";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO dbo.{parentTable} (CustomerId) VALUES (7); INSERT INTO dbo.{oldTableName} (CustomerId) VALUES (7)";
        cmd.ExecuteNonQuery();

        // v2 deploy: rename the table via OldName AND rename its PK and FK.
        var jsonV2 = $$"""
        [
            {
                "Schema": "[dbo]",
                "Name": "[{{parentTable}}]",
                "Columns": [{"Name": "[CustomerId]", "DataType": "INT", "Nullable": false}],
                "Indexes": [{"Name": "[PK_{{parentTable}}]", "PrimaryKey": true, "IndexColumns": "[CustomerId]"}]
            },
            {
                "Schema": "[dbo]",
                "Name": "[{{newTableName}}]",
                "OldName": "[{{oldTableName}}]",
                "Columns": [
                    {"Name": "[OrderId]", "DataType": "INT IDENTITY(1,1)"},
                    {"Name": "[CustomerId]", "DataType": "INT"}
                ],
                "Indexes": [{"Name": "[PK_{{newTableName}}]", "PrimaryKey": true, "IndexColumns": "[OrderId]"}],
                "ForeignKeys": [{"Name": "[FK_{{newTableName}}_Customer]", "Columns": "[CustomerId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[{{parentTable}}]", "RelatedColumns": "[CustomerId]"}]
            }
        ]
        """;
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{jsonV2.Replace("'", "''")}', @DropTablesRemovedFromProduct = 1, @DropUnknownIndexes = 0";
        Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(), "Renaming a table and its constraints via OldName should not error");

        cmd.CommandText = $"SELECT CAST(CASE WHEN OBJECT_ID('dbo.{oldTableName}') IS NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "Old table should no longer exist");
        cmd.CommandText = $"SELECT CustomerId FROM dbo.{newTableName}";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(7), "Data should be preserved after rename");

        // PK renamed to the new name; old-named PK gone; exactly one PK.
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('dbo.{newTableName}') AND type = 'PK'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "Renamed table should have exactly one primary key");
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'PK_{newTableName}'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "PK should exist under its new name");
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'PK_{oldTableName}'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "Old-named PK should be gone");

        // FK renamed to the new name; old-named FK gone.
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_{newTableName}_Customer'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "FK should exist under its new name");
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_{oldTableName}_Customer'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "Old-named FK should be gone");

        conn.Close();
    }

    [Test]
    public void ShouldAddIdentityToExistingColumn()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"AddIdentity_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table WITHOUT identity
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Val INT NOT NULL)
INSERT INTO dbo.{tableName} (Id, Val) VALUES (1, 10), (2, 20)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench WITH identity — requires drop+recreate of column
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT IDENTITY(1, 1)", "Nullable": false},
        {"Name": "[Val]", "DataType": "INT", "Nullable": false}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify identity was applied
        cmd.CommandText = $"SELECT COLUMNPROPERTY(OBJECT_ID('dbo.{tableName}'), 'Id', 'IsIdentity')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "Column should now be IDENTITY");

        // Verify data preserved
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.{tableName}";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2), "Data should be preserved after identity addition");

        conn.Close();
    }

    [Test]
    public void ShouldNotCreateTableInWhatIfMode()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"WhatIfTable_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "INT", "Nullable": false}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @WhatIf = 1, @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"SELECT CAST(CASE WHEN OBJECT_ID('dbo.{tableName}') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "Table should not be created in WhatIf mode");

        conn.Close();
    }

    [Test]
    public void ShouldRemoveIdentityFromExistingColumn()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"RemoveIdentity_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table WITH identity
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT IDENTITY(1,1) NOT NULL, Val INT NOT NULL)
SET IDENTITY_INSERT dbo.{tableName} ON
INSERT INTO dbo.{tableName} (Id, Val) VALUES (1, 10), (2, 20)
SET IDENTITY_INSERT dbo.{tableName} OFF
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify identity exists before quench
        cmd.CommandText = $"SELECT COLUMNPROPERTY(OBJECT_ID('dbo.{tableName}'), 'Id', 'IsIdentity')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "Column should be IDENTITY before quench");

        // Quench WITHOUT identity — should remove it via drop+recreate
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "INT", "Nullable": false}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify identity was removed
        cmd.CommandText = $"SELECT COLUMNPROPERTY(OBJECT_ID('dbo.{tableName}'), 'Id', 'IsIdentity')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "Column should no longer be IDENTITY");

        // Verify data preserved
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.{tableName}";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2), "Data should be preserved after identity removal");

        conn.Close();
    }
}
