// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_IndexOnlyModificationTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldModifyIndexColumns()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxModCols_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with index on Col1
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Col1 INT NOT NULL, Col2 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Test ON dbo.{tableName} ([Col1])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify initial index column
        cmd.CommandText = $@"
SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test' AND ic.is_included_column = 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col1"), "Initial index column should be Col1");

        // IndexOnly quench to change index columns to Col2
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_Test]",
                "IndexColumns": "[Col2]"
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify index column was changed
        cmd.CommandText = $@"
SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test' AND ic.is_included_column = 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col2"), "Index column should be changed to Col2");

        conn.Close();
    }

    [Test]
    public void ShouldModifyIncludeColumns()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxModInc_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with index including Col2
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Col1 INT NOT NULL, Col2 INT NOT NULL, Col3 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Test ON dbo.{tableName} ([Col1]) INCLUDE ([Col2])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // IndexOnly quench to change include columns to Col3
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_Test]",
                "IndexColumns": "[Col1]",
                "IncludeColumns": "[Col3]"
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify include column was changed to Col3
        cmd.CommandText = $@"
SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test' AND ic.is_included_column = 1";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col3"), "Include column should be changed to Col3");

        conn.Close();
    }

    [Test]
    public void ShouldAddIncludeColumnsToExistingIndex()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxAddInc_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with index without include columns
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Col1 INT NOT NULL, Col2 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Test ON dbo.{tableName} ([Col1])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // IndexOnly quench to add include columns
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_Test]",
                "IndexColumns": "[Col1]",
                "IncludeColumns": "[Col2]"
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify include column was added
        cmd.CommandText = $@"
SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test' AND ic.is_included_column = 1";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col2"), "Include column should be added");

        conn.Close();
    }

    [Test]
    public void ShouldModifyFilterExpression()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxModFilter_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with filtered index
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Status INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Test ON dbo.{tableName} ([Status]) WHERE [Status]>(0)
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // IndexOnly quench with changed filter
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_Test]",
                "IndexColumns": "[Status]",
                "FilterExpression": "[Status]>(5)"
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify filter expression was changed
        cmd.CommandText = $@"
SELECT i.filter_definition FROM sys.indexes i
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("[Status]>(5)"), "Filter expression should be updated");

        conn.Close();
    }

    [Test]
    public void ShouldModifyFillFactor()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxModFill_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with index at default fill factor (0 = 100%)
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Col1 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Test ON dbo.{tableName} ([Col1])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // IndexOnly quench with fill factor 80 and UpdateFillFactor enabled
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "UpdateFillFactor": true,
        "Indexes": [
            {
                "Name": "[IDX_Test]",
                "IndexColumns": "[Col1]",
                "FillFactor": 80
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify fill factor was set
        cmd.CommandText = $@"
SELECT i.fill_factor FROM sys.indexes i
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(80), "Fill factor should be 80");

        conn.Close();
    }

    [Test]
    public void ShouldNotModifyFillFactorWhenUpdateFillFactorDisabled()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxNoFill_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with index at default fill factor
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Col1 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Test ON dbo.{tableName} ([Col1])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // IndexOnly quench with fill factor 80 but @UpdateFillFactor = 0 at SP level
        // Note: SP parameter defaults to 1, so must explicitly pass 0 to disable
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_Test]",
                "IndexColumns": "[Col1]",
                "FillFactor": 80
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0, @UpdateFillFactor = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify fill factor was NOT changed (0 = server default = 100%)
        cmd.CommandText = $@"
SELECT i.fill_factor FROM sys.indexes i
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Test'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "Fill factor should remain at default when @UpdateFillFactor = 0");

        conn.Close();
    }

    [Test]
    public void ShouldHandleMultipleIndexModificationsSimultaneously()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IdxMultiMod_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with two indexes
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Col1 INT NOT NULL, Col2 INT NOT NULL, Col3 INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_One ON dbo.{tableName} ([Col1])
CREATE NONCLUSTERED INDEX IDX_Two ON dbo.{tableName} ([Col2])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // IndexOnly quench modifying both indexes simultaneously
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_One]",
                "IndexColumns": "[Col1]",
                "IncludeColumns": "[Col3]"
            },
            {
                "Name": "[IDX_Two]",
                "IndexColumns": "[Col2], [Col3]"
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify IDX_One has include column
        cmd.CommandText = $@"
SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_One' AND ic.is_included_column = 1";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col3"), "IDX_One should have Col3 as include column");

        // Verify IDX_Two has two key columns
        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Two' AND ic.is_included_column = 0";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2), "IDX_Two should have two key columns");

        conn.Close();
    }
}
