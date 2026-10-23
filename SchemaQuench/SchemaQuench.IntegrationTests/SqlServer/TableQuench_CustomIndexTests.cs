// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CustomIndexTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldAlterColumnUsedInCustomIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndex'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndex'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndex'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndex'), 'IDX_NoDependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInCustomIndex", "Column2"), Is.EqualTo("BIGINT"));
        
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnUsedInIndexInclude()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.DropColumnInIndexInclude'), 'Column2', 'AllowsNull') IS NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.DropColumnInIndexInclude'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.DropColumnInIndexInclude'), 'IDX_Dependency', 'IndexId') IS NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.DropColumnInIndexInclude'), 'IDX_NoDependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInCustomIndexInclude()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndexInclude'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndexInclude'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndexInclude'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInCustomIndexInclude'), 'IDX_NoDependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInCustomIndexInclude", "Column2"), Is.EqualTo("BIGINT"));

        // Make sure the custom indexes DO NOT get the ProductName extended property
        cmd.CommandText = @"
SELECT CONVERT(VARCHAR(50), x.[value]) AS [value]
  FROM fn_listextendedproperty(default, 'Schema', 'dbo', 'Table', 'AlterColumnInCustomIndexInclude', 'Index', default) x
  WHERE x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'
";
        Assert.That(cmd.ExecuteScalar(), Is.Null);

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
--TableQuench_ShouldAlterColumnUsedInCustomIndex
CREATE TABLE dbo.AlterColumnInCustomIndex (Column1 INT NOT NULL, Column2 INT NULL)
CREATE INDEX IDX_NoDependency ON dbo.AlterColumnInCustomIndex ([Column1])
CREATE INDEX IDX_Dependency ON dbo.AlterColumnInCustomIndex ([Column2])
--TableQuench_ShouldHandleRemovingColumnUsedInIndexInclude
CREATE TABLE dbo.DropColumnInIndexInclude (Column1 INT NOT NULL, Column2 INT)
CREATE INDEX IDX_NoDependency ON dbo.DropColumnInIndexInclude ([Column1])
CREATE INDEX IDX_Dependency ON dbo.DropColumnInIndexInclude ([Column1]) INCLUDE ([Column2])
--TableQuench_ShouldAlterColumnUsedInCustomIndexInclude
CREATE TABLE dbo.AlterColumnInCustomIndexInclude (Column1 INT NOT NULL, Column2 INT)
CREATE INDEX IDX_NoDependency ON dbo.AlterColumnInCustomIndexInclude ([Column1])
CREATE INDEX IDX_Dependency ON dbo.AlterColumnInCustomIndexInclude ([Column1]) INCLUDE ([Column2])
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnInCustomIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[DropColumnInIndexInclude]",
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
                "Name": "[AlterColumnInCustomIndexInclude]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
