// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Pinning coverage for constraint identifiers that contain a space — whether they
// arise from SQL Server's auto-naming on a spaced table like [Order Details] or are
// explicitly authored. Names with spaces blow up parsing if any DDL emission site
// concatenates them without bracket-wrapping; these tests pin every code path that
// mints DROP / ADD CONSTRAINT against an in-flight constraint name:
//   * ModifiedTableQuench DROP path                  (sys.* names — never bracketed)
//   * MissingIndexesAndConstraintsQuench ADD CHECK   (#CheckConstraints — JSON skips fn_SafeBracketWrap)
//   * ForeignKeyQuench ADD FK                        (#ForeignKeys — pre-bracketed via fn_SafeBracketWrap on load)
//   * MissingIndexesAndConstraintsQuench ADD PK/UQ   (#Indexes — pre-bracketed via fn_SafeBracketWrap on load)
//   * IndexOnlyQuench ADD PK/UQ                      (#Indexes — pre-bracketed via fn_SafeBracketWrap on load)
// The first two were broken until SchemaSmith.ModifiedTableQuench / .MissingIndexesAndConstraintsQuench
// were fixed to bracket-wrap explicitly; the latter three are guarded by the parser's
// fn_SafeBracketWrap pre-pass and these tests catch regressions if that protection erodes.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_SystemGeneratedConstraintTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldModifyColumnLevelCheckOnSpacedTable()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"Spaced Check {uniqueId}"; // space in name → space in auto-named CHECK

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Pre-existing table: inline column CHECK with system-generated name (will contain a space).
        cmd.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] ([Discount] REAL NOT NULL CHECK ([Discount]>=(0)))
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Declared state: same column, different CHECK expression — forces the
        // existing system-named constraint to be dropped and a new one created.
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {
            "Name": "[Discount]",
            "DataType": "REAL",
            "Nullable": false,
            "CheckExpression": "[Discount]>=(0) AND [Discount]<=(1)"
        }
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // The old auto-named CHECK should be dropped, a single new CHECK should remain
        // with the declared expression.
        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.check_constraints WITH (NOLOCK)
WHERE parent_object_id = OBJECT_ID('[dbo].[{tableName}]')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1"), "Exactly one CHECK constraint should remain after quench");

        cmd.CommandText = $@"
SELECT SchemaSmith.fn_StripParenWrapping([definition])
  FROM sys.check_constraints WITH (NOLOCK)
 WHERE parent_object_id = OBJECT_ID('[dbo].[{tableName}]')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Discount]>=(0) AND [Discount]<=(1)"), "CHECK expression should match the declared state");

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyTableLevelCheckOnSpacedTable()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"Spaced Table Check {uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Pre-existing table-level CHECK with an explicitly named constraint
        // whose name contains a space — round-trip should still bracket-wrap correctly.
        var existingCheckName = $"CK Spaced {uniqueId}";
        cmd.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] ([Id] INT NOT NULL, [Col2] INT NULL)
ALTER TABLE [dbo].[{tableName}] ADD CONSTRAINT [{existingCheckName}] CHECK ([Col2]>[Id])
EXEC sp_addextendedproperty @name = N'ProductName', @value = '{productName}', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = '{tableName}'
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Declared state keeps the constraint name but changes the expression →
        // forces a drop-and-recreate of the same-named constraint.
        var json = $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Col2]", "DataType": "INT", "Nullable": true}
    ],
    "CheckConstraints": [
        {"Name": "{{existingCheckName}}", "Expression": "[Col2]>=[Id]"}
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $@"
SELECT SchemaSmith.fn_StripParenWrapping([definition])
  FROM sys.check_constraints WITH (NOLOCK)
 WHERE parent_object_id = OBJECT_ID('[dbo].[{tableName}]')
   AND [name] = '{existingCheckName}'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Col2]>=[Id]"), "Table-level CHECK expression should match the declared state");

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddForeignKeyWithSpacedName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"FKSpaced{uniqueId}";
        var fkName = $"FK Spaced {uniqueId}"; // space in FK name → exercises ForeignKeyQuench

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Pre-existing self-referential table with auto-named PK, no FK yet.
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL PRIMARY KEY, RefId INT NULL)
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
        {"Name": "[RefId]", "DataType": "INT", "Nullable": true}
    ],
    "ForeignKeys": [
        {
            "Name": "{{fkName}}",
            "Columns": "[RefId]",
            "RelatedTableSchema": "dbo",
            "RelatedTable": "[{{tableName}}]",
            "RelatedColumns": "[Id]"
        }
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.foreign_keys WITH (NOLOCK)
 WHERE parent_object_id = OBJECT_ID('dbo.{tableName}') AND [name] = '{fkName}'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1"), "FK with spaced name should have been added");

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddUniqueConstraintWithSpacedName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"UQSpaced{uniqueId}";
        var uqName = $"UQ Spaced {uniqueId}"; // space in name → MissingIndexesAndConstraintsQuench

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Pre-existing table with auto-named PK, no UNIQUE constraint yet.
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL PRIMARY KEY, Slug NVARCHAR(50) NOT NULL)
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
        {"Name": "[Slug]", "DataType": "NVARCHAR(50)", "Nullable": false}
    ],
    "Indexes": [
        {"Name": "{{uqName}}", "UniqueConstraint": true, "IndexColumns": "[Slug]"}
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.key_constraints WITH (NOLOCK)
 WHERE parent_object_id = OBJECT_ID('dbo.{tableName}') AND [name] = '{uqName}' AND [type] = 'UQ'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1"), "Unique constraint with spaced name should have been added");

        conn.Close();
    }

    [Test]
    public void IndexOnlyQuench_ShouldAddUniqueConstraintWithSpacedName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"UQIdxOnly{uniqueId}";
        var uqName = $"UQ Idx Only {uniqueId}"; // space in name → IndexOnlyQuench

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Pre-existing table with auto-named PK, no UNIQUE constraint yet.
        // IndexOnlyQuench has its own ADD CONSTRAINT emission separate from TableQuench's.
        cmd.CommandText = $@"
CREATE TABLE dbo.{tableName} (Id INT NOT NULL PRIMARY KEY, Slug NVARCHAR(50) NOT NULL)
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
        {"Name": "[Slug]", "DataType": "NVARCHAR(50)", "Nullable": false}
    ],
    "Indexes": [
        {"Name": "{{uqName}}", "UniqueConstraint": true, "IndexColumns": "[Slug]"}
    ]
}
""";
        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.key_constraints WITH (NOLOCK)
 WHERE parent_object_id = OBJECT_ID('dbo.{tableName}') AND [name] = '{uqName}' AND [type] = 'UQ'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1"), "Unique constraint with spaced name should have been added");

        conn.Close();
    }
}
