// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_FilteredIndexTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldCreateFilteredIndex()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"FilterIdx_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table without index
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Status INT NOT NULL, Val NVARCHAR(100) NULL)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with filtered index
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Status]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": true}
    ],
    "Indexes": [
        {
            "Name": "[IDX_FilteredStatus]",
            "IndexColumns": "[Status]",
            "FilterExpression": "[Status]>(0)"
        }
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify filtered index exists
        cmd.CommandText = $@"
SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.{tableName}'), 'IDX_FilteredStatus', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "Filtered index should exist");

        // Verify filter expression
        cmd.CommandText = $@"
SELECT i.filter_definition FROM sys.indexes i
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_FilteredStatus'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("[Status]>(0)"), "Filter expression should match");

        conn.Close();
    }

    [Test]
    public void ShouldCreateFilteredIndexWithIncludeColumns()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"FilterIdxInc_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, IsActive BIT NOT NULL, Name NVARCHAR(100) NULL, Email NVARCHAR(200) NULL)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with filtered index that has include columns
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[IsActive]", "DataType": "BIT", "Nullable": false},
        {"Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": true},
        {"Name": "[Email]", "DataType": "NVARCHAR(200)", "Nullable": true}
    ],
    "Indexes": [
        {
            "Name": "[IDX_ActiveUsers]",
            "IndexColumns": "[Name]",
            "IncludeColumns": "[Email]",
            "FilterExpression": "[IsActive]=(1)"
        }
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify index exists with filter
        cmd.CommandText = $@"
SELECT i.filter_definition FROM sys.indexes i
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_ActiveUsers'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("[IsActive]=(1)"), "Filter expression should match");

        // Verify include column exists
        cmd.CommandText = $@"
SELECT COL_NAME(ic.[object_id], ic.column_id) FROM sys.index_columns ic
JOIN sys.indexes i ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_ActiveUsers' AND ic.is_included_column = 1";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Email"), "Include column should exist");

        conn.Close();
    }

    [Test]
    public void ShouldModifyFilteredIndexViaIndexOnly()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ModFilterIdx_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with a filtered index
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Status INT NOT NULL)
CREATE NONCLUSTERED INDEX IDX_Filtered ON dbo.{tableName} ([Status]) WHERE [Status]>(0)
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench via IndexOnly with a different filter expression
        var json = $$"""
[
    {
        "Schema": "[dbo]",
        "Name": "[{{tableName}}]",
        "Indexes": [
            {
                "Name": "[IDX_Filtered]",
                "IndexColumns": "[Status]",
                "FilterExpression": "[Status]>(10)"
            }
        ]
    }
]
""";

        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify filter expression was updated
        cmd.CommandText = $@"
SELECT i.filter_definition FROM sys.indexes i
WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}') AND i.[name] = 'IDX_Filtered'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("[Status]>(10)"), "Filter expression should be updated");

        conn.Close();
    }
}
