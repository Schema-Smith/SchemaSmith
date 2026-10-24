// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DataMaskingTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldAddDataMaskToExistingColumn()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"AddMask_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table without masking
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Email NVARCHAR(200) NOT NULL)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify no masking exists
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "No masking should exist before quench");

        // Quench with data mask
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Email]", "DataType": "NVARCHAR(200)", "Nullable": false, "DataMaskFunction": "email()"}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify masking was applied
        cmd.CommandText = $"SELECT masking_function FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'Email'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("email()"), "Data mask should be applied");

        conn.Close();
    }

    [Test]
    public void ShouldRemoveDataMaskFromExistingColumn()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"RemoveMask_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table WITH masking
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, SSN NVARCHAR(11) NOT NULL)
ALTER TABLE dbo.{tableName} ALTER COLUMN SSN ADD MASKED WITH (FUNCTION = 'partial(0,""XXX-XX-"",4)')
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify masking exists
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'SSN'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "Masking should exist before quench");

        // Quench without data mask — should remove it
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[SSN]", "DataType": "NVARCHAR(11)", "Nullable": false}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify masking was removed
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'SSN'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(0), "Data mask should be removed");

        conn.Close();
    }

    [Test]
    public void ShouldModifyDataMaskOnExistingColumn()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"ModifyMask_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with default() mask
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Phone NVARCHAR(20) NOT NULL)
ALTER TABLE dbo.{tableName} ALTER COLUMN Phone ADD MASKED WITH (FUNCTION = 'default()')
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Verify original mask
        cmd.CommandText = $"SELECT masking_function FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'Phone'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("default()"), "Original mask should exist");

        // Quench with different mask function
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Phone]", "DataType": "NVARCHAR(20)", "Nullable": false, "DataMaskFunction": "partial(0,\"XXX-\",4)"}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify mask was changed
        cmd.CommandText = $"SELECT masking_function FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'Phone'";
        var result = cmd.ExecuteScalar()?.ToString();
        Assert.That(result, Does.Contain("partial"), "Mask function should be updated to partial");

        conn.Close();
    }

    [Test]
    public void ShouldAddDataMaskToNewColumnOnExistingTable()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"MaskNewCol_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table with one column
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL)
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with new masked column
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[CreditCard]", "DataType": "NVARCHAR(20)", "Nullable": true, "DataMaskFunction": "partial(0,\"XXXX-XXXX-XXXX-\",4)"}
    ]
}
""";

        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify new column has mask
        cmd.CommandText = $"SELECT masking_function FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'CreditCard'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("partial"), "New column should have data mask applied");

        conn.Close();
    }

    [Test]
    public void ShouldPreserveMaskOnReQuenchWithNoChanges()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"PreserveMask_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create table and quench with mask
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Val NVARCHAR(100) NOT NULL)
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
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": false, "DataMaskFunction": "default()"}
    ]
}
""";

        // First quench — apply mask
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Second quench — same definition, should not error
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify mask still present
        cmd.CommandText = $"SELECT masking_function FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = 'Val'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("default()"), "Mask should be preserved on re-quench");

        conn.Close();
    }
}
