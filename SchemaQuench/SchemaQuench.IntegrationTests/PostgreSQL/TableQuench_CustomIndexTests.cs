// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CustomIndexTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldAlterColumnUsedInCustomIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'CustomIndexTests' AND table_name = 'AlterColumnInCustomIndex' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'CustomIndexTests' AND table_name = 'AlterColumnInCustomIndex' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'CustomIndexTests' AND tablename = 'AlterColumnInCustomIndex' AND indexname = 'IDX_Dependency')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'CustomIndexTests' AND tablename = 'AlterColumnInCustomIndex' AND indexname = 'IDX_NoDependency')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInCustomIndex", "Column2"), Is.EqualTo("INT8"));
        
        conn.Close();
    }

    private string GetColumnDataType(IDbCommand cmd, string tableName, string columnName)
    {
        return GetColumnDataType(cmd, "CustomIndexTests", tableName, columnName);
    }


    [Test]
    public void TableQuench_ShouldHandleRemovingColumnUsedInIndexInclude()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'CustomIndexTests' AND table_name = 'DropColumnInIndexInclude' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'CustomIndexTests' AND table_name = 'DropColumnInIndexInclude' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'CustomIndexTests' AND tablename = 'DropColumnInIndexInclude' AND indexname = 'IDX_Dependency2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'CustomIndexTests' AND tablename = 'DropColumnInIndexInclude' AND indexname = 'IDX_NoDependency2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInCustomIndexInclude()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'CustomIndexTests' AND table_name = 'AlterColumnInCustomIndexInclude' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'CustomIndexTests' AND table_name = 'AlterColumnInCustomIndexInclude' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'CustomIndexTests' AND tablename = 'AlterColumnInCustomIndexInclude' AND indexname = 'IDX_Dependency3')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'CustomIndexTests' AND tablename = 'AlterColumnInCustomIndexInclude' AND indexname = 'IDX_NoDependency3')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        // Make sure the custom indexes DO NOT get the ProductName extended property
        cmd.CommandText = @"SELECT ""ProductName"" FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""Schema"" = 'CustomIndexTests' AND ""TableName"" = 'AlterColumnInCustomIndexInclude' AND ""IndexName"" IS NOT NULL;";
        Assert.That(cmd.ExecuteScalar(), Is.Null);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInCustomIndexInclude", "Column2"), Is.EqualTo("INT8"));

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
CREATE SCHEMA ""CustomIndexTests"";
--TableQuench_ShouldAlterColumnUsedInCustomIndex
CREATE TABLE ""CustomIndexTests"".""AlterColumnInCustomIndex"" (""Column1"" INT NOT NULL, ""Column2"" INT NULL);
CREATE INDEX ""IDX_NoDependency"" ON ""CustomIndexTests"".""AlterColumnInCustomIndex"" (""Column1"");
CREATE INDEX ""IDX_Dependency"" ON ""CustomIndexTests"".""AlterColumnInCustomIndex"" (""Column2"");
--TableQuench_ShouldHandleRemovingColumnUsedInIndexInclude
CREATE TABLE ""CustomIndexTests"".""DropColumnInIndexInclude"" (""Column1"" INT NOT NULL, ""Column2"" INT);
CREATE INDEX ""IDX_NoDependency2"" ON ""CustomIndexTests"".""DropColumnInIndexInclude"" (""Column1"");
CREATE INDEX ""IDX_Dependency2"" ON ""CustomIndexTests"".""DropColumnInIndexInclude"" (""Column1"") INCLUDE (""Column2"");
--TableQuench_ShouldAlterColumnUsedInCustomIndexInclude
CREATE TABLE ""CustomIndexTests"".""AlterColumnInCustomIndexInclude"" (""Column1"" INT NOT NULL, ""Column2"" INT);
CREATE INDEX ""IDX_NoDependency3"" ON ""CustomIndexTests"".""AlterColumnInCustomIndexInclude"" (""Column1"");
CREATE INDEX ""IDX_Dependency3"" ON ""CustomIndexTests"".""AlterColumnInCustomIndexInclude"" (""Column1"") INCLUDE (""Column2"");
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "CustomIndexTests",
                "Name": "AlterColumnInCustomIndex",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "CustomIndexTests",
                "Name": "DropColumnInIndexInclude",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "CustomIndexTests",
                "Name": "AlterColumnInCustomIndexInclude",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
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
