// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ModifyIndexTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldModifyIndexWhenFilterExpressionChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping(filter_definition) FROM sys.indexes si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexFilterExpression') AND [Name] = 'IDX_Filtered'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Column1]>(50)"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.indexes si WITH (NOLOCK) JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = si.[object_id] AND ic.[index_id] = si.[index_id] WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexColumnList') AND [Name] = 'IDX_ColumnList'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListChangesIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.indexes si WITH (NOLOCK) JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = si.[object_id] AND ic.[index_id] = si.[index_id] WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexColumnListIO') AND [Name] = 'IDX_ColumnList'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListOrderChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
SELECT (SELECT STRING_AGG('[' + COL_NAME(ic.[object_id], ic.column_id) + ']', ',') WITHIN GROUP (ORDER BY key_ordinal)
          FROM sys.index_columns  ic WITH (NOLOCK)
          WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 0)
  FROM sys.indexes si WITH (NOLOCK)
  WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexColumnListOrder') 
    AND [Name] = 'IDX_ColumnList'
";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Column2],[Column1]"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenIncludeListChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.indexes si WITH (NOLOCK) JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = si.[object_id] AND ic.[index_id] = si.[index_id] AND ic.is_included_column = 1  WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexIncludeList') AND [Name] = 'IDX_IncludeList'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column3"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesClustered()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN si.[type] IN (1, 5) THEN 1 ELSE 0 END AS BIT) FROM sys.indexes si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexBecomesClustered') AND [Name] = 'IDX_BecomesClustered'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesNonClustered()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN si.[type] IN (1, 5) THEN 1 ELSE 0 END AS BIT) FROM sys.indexes si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexBecomesNonClustered') AND [Name] = 'IDX_BecomesNonClustered'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesUnique()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT is_unique FROM sys.indexes si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexBecomesUnique') AND [Name] = 'IDX_BecomesUnique'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesNonUnique()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT is_unique FROM sys.indexes si WITH (NOLOCK) WHERE si.[object_id] = OBJECT_ID('dbo.ModifyIndexBecomesNonUnique') AND [Name] = 'IDX_BecomesNonUnique'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenColumnsChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.fulltext_index_columns ic WITH (NOLOCK) WHERE ic.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexColumnList')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column3"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenColumnsChangeFromEmpty()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.fulltext_index_columns ic WITH (NOLOCK) WHERE ic.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexColumnListFromEmpty')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column3"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenCatalogChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT c.[name] FROM sys.fulltext_indexes fi WITH (NOLOCK) JOIN sys.fulltext_catalogs c WITH (NOLOCK) ON fi.[fulltext_catalog_id] = c.[fulltext_catalog_id] WHERE fi.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexCatalog')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("FT_Catalog2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenKeyIndexChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT i.[name] FROM sys.fulltext_indexes fi WITH (NOLOCK) JOIN sys.indexes i WITH (NOLOCK) ON i.[object_id] = fi.[object_id] AND i.[index_id] = fi.[unique_index_id] WHERE fi.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexKeyIndex')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("IDX_KeyIndex2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenKeyChangeTrackingChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT change_tracking_state_desc FROM sys.fulltext_indexes fi WITH (NOLOCK) WHERE fi.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexChangeTracking')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("AUTO"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenAddStopList()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT fs.[Name] FROM sys.fulltext_indexes fi WITH (NOLOCK) JOIN sys.fulltext_stoplists fs WITH (NOLOCK) ON fs.stoplist_id = fi.stoplist_id WHERE fi.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexStopListAdd')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("SL_Test"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenRemoveStopList()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT fs.[Name] FROM sys.fulltext_indexes fi WITH (NOLOCK) JOIN sys.fulltext_stoplists fs WITH (NOLOCK) ON fs.stoplist_id = fi.stoplist_id WHERE fi.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexStopListRemove')";
        Assert.That(cmd.ExecuteScalar(), Is.Null);

        conn.Close();
    }

    [Test]
    public void ShouldModifyFullTextIndexWhenChangeStopList()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT fs.[Name] FROM sys.fulltext_indexes fi WITH (NOLOCK) JOIN sys.fulltext_stoplists fs WITH (NOLOCK) ON fs.stoplist_id = fi.stoplist_id WHERE fi.[object_id] = OBJECT_ID('dbo.ModifyFullTextIndexStopListChange')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("SL_Test2"));

        conn.Close();
    }

    [Test]
    public void ShouldHandleModiyingPrimaryKeyWhenXMLIndexesExist()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT [Name] FROM sys.xml_indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.ModifyPKWithXMLIndexes') AND xml_index_type = 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("XI_Primary"));

        cmd.CommandText = "SELECT [Name] FROM sys.xml_indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.ModifyPKWithXMLIndexes') AND xml_index_type > 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("XI_Secondary_Path"));

        conn.Close();
    }

    [Test]
    public void ShouldModifySecondaryXmlIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        
        cmd.CommandText = "SELECT [secondary_type_desc] FROM sys.xml_indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.ModifySecondaryXmlIndex') AND xml_index_type > 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("VALUE"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyPrimaryXmlIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT (SELECT COL_NAME(ic.[Object_id], ic.column_id) FROM sys.index_columns ic WHERE ic.index_id = xi.index_id AND ic.[object_id] = xi.[object_id]) FROM sys.xml_indexes xi WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.ModifyPrimaryXmlIndex')  AND xml_index_type = 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column4"));

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
--ShouldModifyIndexWhenFilterExpressionChanges
CREATE TABLE dbo.ModifyIndexFilterExpression (Column1 INT NOT NULL)
CREATE INDEX IDX_Filtered ON dbo.ModifyIndexFilterExpression ([Column1]) WHERE [Column1]>(20)
--ShouldModifyIndexWhenSortColumnListChanges
CREATE TABLE dbo.ModifyIndexColumnList (Column1 INT NOT NULL, Column2 INT NOT NULL)
CREATE INDEX IDX_ColumnList ON dbo.ModifyIndexColumnList ([Column1])
--ShouldModifyIndexWhenSortColumnListOrderChanges
CREATE TABLE dbo.ModifyIndexColumnListOrder (Column1 INT NOT NULL, Column2 INT NOT NULL)
CREATE INDEX IDX_ColumnList ON dbo.ModifyIndexColumnListOrder ([Column1], [Column2])
--ShouldModifyIndexWhenIncludeListChanges
CREATE TABLE dbo.ModifyIndexIncludeList (Column1 INT NOT NULL, Column2 INT NOT NULL, Column3 INT NOT NULL)
CREATE INDEX IDX_IncludeList ON dbo.ModifyIndexIncludeList ([Column1]) INCLUDE ([Column2])
--ShouldModifyIndexWhenItBecomesClustered
CREATE TABLE dbo.ModifyIndexBecomesClustered (Column1 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_BecomesClustered ON dbo.ModifyIndexBecomesClustered ([Column1])
--ShouldModifyIndexWhenItBecomesNonClustered
CREATE TABLE dbo.ModifyIndexBecomesNonClustered (Column1 INT NOT NULL)
CREATE CLUSTERED INDEX IDX_BecomesNonClustered ON dbo.ModifyIndexBecomesNonClustered ([Column1])
--ShouldModifyIndexWhenItBecomesUnique
CREATE TABLE dbo.ModifyIndexBecomesUnique (Column1 INT NOT NULL)
--ShouldModifyIndexWhenItBecomesNonUnique
CREATE INDEX IDX_BecomesUnique ON dbo.ModifyIndexBecomesUnique ([Column1])
CREATE TABLE dbo.ModifyIndexBecomesNonUnique (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 INT NOT NULL)
CREATE UNIQUE INDEX IDX_BecomesNonUnique ON dbo.ModifyIndexBecomesNonUnique ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyIndexBecomesNonUnique (Column2) KEY INDEX IDX_BecomesNonUnique ON FT_Catalog WITH CHANGE_TRACKING = OFF
ALTER TABLE dbo.ModifyIndexBecomesNonUnique ADD CONSTRAINT FK_ModifyIndexBecomesNonUnique_SelfRef FOREIGN KEY (Column3) REFERENCES dbo.ModifyIndexBecomesNonUnique (Column1)
--ShouldModifyFullTextIndexWhenColumnsChange
CREATE TABLE dbo.ModifyFullTextIndexColumnList (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexColumnList ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexColumnList (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--ShouldModifyFullTextIndexWhenColumnsChangeFromEmpty
CREATE TABLE dbo.ModifyFullTextIndexColumnListFromEmpty (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexColumnListFromEmpty ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexColumnListFromEmpty KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--ShouldModifyFullTextIndexWhenCatalogChanges
CREATE TABLE dbo.ModifyFullTextIndexCatalog (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexCatalog ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexCatalog (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--ShouldModifyFullTextIndexWhenKeyIndexChanges
CREATE TABLE dbo.ModifyFullTextIndexKeyIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexKeyIndex ([Column1])
CREATE UNIQUE INDEX IDX_KeyIndex2 ON dbo.ModifyFullTextIndexKeyIndex ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexKeyIndex (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--ShouldModifyFullTextIndexWhenKeyChangeTrackingChanges
CREATE TABLE dbo.ModifyFullTextIndexChangeTracking (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexChangeTracking ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexChangeTracking (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--ShouldModifyFullTextIndexWhenAddStopList
CREATE TABLE dbo.ModifyFullTextIndexStopListAdd (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexStopListAdd ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexStopListAdd (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--ShouldModifyFullTextIndexWhenRemoveStopList
CREATE TABLE dbo.ModifyFullTextIndexStopListRemove (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexStopListRemove ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexStopListRemove (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF, STOPLIST = [SL_Test]
--ShouldModifyFullTextIndexWhenChangeStopList
CREATE TABLE dbo.ModifyFullTextIndexStopListChange (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_KeyIndex ON dbo.ModifyFullTextIndexStopListChange ([Column1])
CREATE FULLTEXT INDEX ON dbo.ModifyFullTextIndexStopListChange (Column2) KEY INDEX IDX_KeyIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF, STOPLIST = [SL_Test]
--ShouldHandleModiyingClusteredIndexWhenXMLIndexesExist
CREATE TABLE dbo.ModifyPKWithXMLIndexes (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 XML NULL, CONSTRAINT PK_ModifyPKWithXMLIndexes PRIMARY KEY CLUSTERED (Column1))
CREATE PRIMARY XML INDEX [XI_Primary] ON dbo.ModifyPKWithXMLIndexes (Column3)
CREATE XML INDEX [XI_Secondary_Path] ON dbo.ModifyPKWithXMLIndexes (Column3) USING XML INDEX [XI_Primary] FOR PATH 
--ShouldModifySecondaryXmlIndex
CREATE TABLE dbo.ModifySecondaryXmlIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 XML NULL, CONSTRAINT PK_ModifySecondaryXmlIndex PRIMARY KEY CLUSTERED (Column1))
CREATE PRIMARY XML INDEX [XI_Primary] ON dbo.ModifySecondaryXmlIndex (Column3)
CREATE XML INDEX [XI_Secondary_Path] ON dbo.ModifySecondaryXmlIndex (Column3) USING XML INDEX [XI_Primary] FOR PATH 
--ShouldModifyPrimaryXmlIndex
CREATE TABLE dbo.ModifyPrimaryXmlIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 XML NULL, Column4 XML NULL, CONSTRAINT PK_ModifyPrimaryXmlIndex PRIMARY KEY CLUSTERED (Column1))
CREATE PRIMARY XML INDEX [XI_Primary] ON dbo.ModifyPrimaryXmlIndex (Column3)

-- Index Only
--ShouldModifyIndexWhenSortColumnListChangesIndexOnly
CREATE TABLE dbo.ModifyIndexColumnListIO (Column1 INT NOT NULL, Column2 INT NOT NULL)
CREATE INDEX IDX_ColumnList ON dbo.ModifyIndexColumnListIO ([Column1])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexFilterExpression]",
                "Columns": [
                    {
                        "Name": "[Column1]",
                        "DataType": "INT",
                        "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                        "Name": "[IDX_Filtered]",
                        "IndexColumns": "[Column1]",
                        "FilterExpression": "[Column1]>(50)"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexColumnList]",
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
                "Indexes": [
                    {
                    "Name": "[IDX_ColumnList]",
                    "IndexColumns": "[Column2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexColumnListOrder]",
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
                "Indexes": [
                    {
                        "Name": "[IDX_ColumnList]",
                        "IndexColumns": "[Column2], [Column1]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexIncludeList]",
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
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_IncludeList]",
                      "IndexColumns": "[Column1]",
                      "IncludeColumns": "[Column3]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexBecomesClustered]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_BecomesClustered]",
                      "IndexColumns": "[Column1]",
                      "Clustered": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexBecomesNonClustered]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_BecomesNonClustered]",
                      "IndexColumns": "[Column1]",
                      "Clustered": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexBecomesUnique]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[IDX_BecomesUnique]",
                      "IndexColumns": "[Column1]",
                      "Unique": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyIndexBecomesNonUnique]",
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
                "Indexes": [
                    {
                      "Name": "[IDX_BecomesNonUnique]",
                      "IndexColumns": "[Column1]",
                      "Unique": false
                    },
                    {
                      "Name": "[IDX_NewUnique]",
                      "IndexColumns": "[Column1]",
                      "Unique": true
                    }
                ],
                "FullTextIndex": {
                    "FullTextCatalog": "FT_Catalog",
                    "KeyIndex": "IDX_NewUnique",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexColumnList]",
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
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    }
                ],
                "FullTextIndex": {
                    "FullTextCatalog": "FT_Catalog",
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column3]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexColumnListFromEmpty]",
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
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    }
                ],
                "FullTextIndex": {
                    "FullTextCatalog": "FT_Catalog",
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column3]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexCatalog]",
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
                    "FullTextCatalog": "FT_Catalog2",
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexKeyIndex]",
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
                    "KeyIndex": "IDX_KeyIndex2",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexChangeTracking]",
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
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "AUTO",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexStopListAdd]",
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
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]",
                    "StopList": "SL_Test"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexStopListRemove]",
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
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFullTextIndexStopListChange]",
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
                    "KeyIndex": "IDX_KeyIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]",
                    "StopList": "SL_Test2"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyPKWithXMLIndexes]",
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
                      "Name": "[PK_ModifyPKWithXMLIndexes]",
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
                "Name": "[ModifySecondaryXmlIndex]",
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
                      "Name": "[PK_ModifySecondaryXmlIndex]",
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
                      "SecondaryIndexType": "VALUE"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyPrimaryXmlIndex]",
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
                    },
                    {
                      "Name": "[Column4]",
                      "DataType": "XML",
                      "Nullable": true
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[PK_ModifyPrimaryXmlIndex]",
                      "IndexColumns": "[Column1]",
                      "Clustered": true,
                      "PrimaryKey": true,
                      "Unique": true
                    }
                ],
                "XmlIndexes": [
                    {
                      "Name": "[XI_Primary]",
                      "Column": "[Column4]",
                      "IsPrimary": true
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
                "Name": "[ModifyIndexColumnListIO]",
                "Indexes": [
                    {
                      "Name": "[IDX_ColumnList]",
                      "IndexColumns": "[Column2]"
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json, indexOnly: true);

        conn.Close();
    }
}
