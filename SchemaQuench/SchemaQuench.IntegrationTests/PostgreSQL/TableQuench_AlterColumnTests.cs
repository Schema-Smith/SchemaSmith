// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using System.Data;
using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_AlterColumnTests : BaseTableQuenchTests
{
    [TestCase("Col1", "INT8")]
    [TestCase("Col2", "BPCHAR(20)")]
    [TestCase("Col4", "VARCHAR(20)")]
    [TestCase("Col6", "VARCHAR")]
    [TestCase("Col8", "VARCHAR(100)")]
    [TestCase("Col10", "TIMESTAMP(5)")]
    [TestCase("Col11", "NUMERIC(12, 3)")]
    [TestCase("Col12", "NUMERIC(10, 2)")]
    [TestCase("Col13", "NUMERIC(12, 3)")]
    [TestCase("Col14", "NUMERIC(10, 2)")]
    [TestCase("Col17", "\"PUBLIC\".\"FLAG\"")]
    [TestCase("Col18", "BOOL")]
    public void TableQuench_ShouldModifyColumnForChangeDataType(string colName, string expectedType)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "ChangeType", colName), Is.EqualTo(expectedType));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyColumnNullability()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT CASE WHEN c.is_nullable = 'YES' THEN TRUE ELSE FALSE END 
                              FROM information_schema.columns c 
                              WHERE table_schema = 'AlterColumnTests' AND table_name = 'ChangeNullability' AND column_name = 'Column1'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleGoingToGeneratedColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT c.is_generated
                              FROM information_schema.columns c 
                              WHERE table_schema = 'AlterColumnTests' AND table_name = 'ColumnToGenerated' AND column_name = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ALWAYS"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleGoingFromGeneratedColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT c.is_generated
                              FROM information_schema.columns c 
                              WHERE table_schema = 'AlterColumnTests' AND table_name = 'ColumnFromGenerated' AND column_name = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("NEVER"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleChangeGenerationExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT c.is_generated
                              FROM information_schema.columns c 
                              WHERE table_schema = 'AlterColumnTests' AND table_name = 'ChangeGenerationExpression' AND column_name = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ALWAYS"));

        cmd.CommandText = @"SELECT c.generation_expression
                              FROM information_schema.columns c 
                              WHERE table_schema = 'AlterColumnTests' AND table_name = 'ChangeGenerationExpression' AND column_name = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("'NewResult'::character varying"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnInIndex' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnInIndex' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AlterColumnTests' AND tablename = 'AlterColumnInIndex' AND indexname = 'IDX_Dependency')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AlterColumnTests' AND tablename = 'AlterColumnInIndex' AND indexname = 'IDX_NoDependency')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInIndex", "Column2"), Is.EqualTo("INT8"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInIndexInclude()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnInIndexInclude' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnInIndexInclude' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AlterColumnTests' AND tablename = 'AlterColumnInIndexInclude' AND indexname = 'IDX_Dependency2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AlterColumnTests' AND tablename = 'AlterColumnInIndexInclude' AND indexname = 'IDX_NoDependency2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInIndexInclude", "Column2"), Is.EqualTo("INT8"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInUniqueIndexReferencedByForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnInUniqueIndexInFK' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnInUniqueIndexInFK' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'AlterColumnTests' AND tablename = 'AlterColumnInUniqueIndexInFK' AND indexname = 'IDX_Dependency3')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_AlterColumnInUniqueIndexInFK_SelfRef' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithDefault' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithDefault' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithDefault", "Column2"), Is.EqualTo("INT8"));

        cmd.CommandText = "SELECT \"SchemaSmith\".\"StripParenWrapping\"(column_default) FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithDefault' AND column_name = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithTableCheckConstraint' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithTableCheckConstraint' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithTableCheckConstraint", "Column2"), Is.EqualTo("INT8"));

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'CK_AlterColumnWithTableCheckConstraint_Dependency' AND contype = 'c')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_AlterColumnWithStatistics()
    {
        if (PgServerMajor() < 14) Assert.Ignore("Expression statistics (CREATE STATISTICS on an expression) require PostgreSQL 14+.");
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithStatistics' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithStatistics' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithStatistics", "Column2"), Is.EqualTo("INT8"));

        cmd.CommandText = "SELECT stxname FROM pg_statistic_ext WHERE stxrelid = ('\"AlterColumnTests\".\"AlterColumnWithStatistics\"')::regclass AND stxname = 'ST_Dependency'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ST_Dependency"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithIndexFilterExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithIndexFilter' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithIndexFilter' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithIndexFilter", "Column2"), Is.EqualTo("INT8"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithFK' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithFK' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithFK", "Column2"), Is.EqualTo("INT8"));

        conn.Close();
    }

    [Test]
    public void TableQuench_AlterColumnWithGenerationExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithGenerated' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'AlterColumnWithGenerated' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithGenerated", "Column2"), Is.EqualTo("INT8"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyColumnCollation()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        
        cmd.CommandText = "SELECT pg_database.datcollate AS default_collation FROM pg_catalog.pg_database WHERE pg_database.datname = pg_catalog.current_database();";
        var dbCollation = cmd.ExecuteScalar() as string;
        Assert.That(dbCollation, Is.Not.EqualTo("az-Latn-x-icu"));

        cmd.CommandText = "SELECT collation_name FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'ModifyColumnCollation' AND column_name = 'Column1'";
        Assert.That(cmd.ExecuteScalar() as string, Is.Null);

        cmd.CommandText = "SELECT collation_name FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'ModifyColumnCollation' AND column_name = 'Column2'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("bs-Latn-x-icu"));

        cmd.CommandText = "SELECT collation_name FROM information_schema.columns WHERE table_schema = 'AlterColumnTests' AND table_name = 'ModifyColumnCollation' AND column_name = 'Column3'";
        Assert.That(cmd.ExecuteScalar() as string, Is.Null);
        conn.Close();
    }

    private string GetColumnDataType(IDbCommand cmd, string tableName, string columnName)
    {
        return GetColumnDataType(cmd,"AlterColumnTests", tableName, columnName);
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE SCHEMA ""AlterColumnTests"";
--TableQuench_ShouldModifyColumnForChangeDataType
CREATE TABLE ""AlterColumnTests"".""ChangeType""
  (""Col1"" INT NOT NULL, ""Col2"" CHAR(10) NOT NULL, ""Col4"" VARCHAR(10) NOT NULL, ""Col6"" VARCHAR(10) NOT NULL, 
   ""Col10"" TIMESTAMP(3) NOT NULL, ""Col11"" DECIMAL(12, 2) NOT NULL, 
   ""Col12"" DECIMAL(12, 2) NOT NULL, ""Col13"" NUMERIC(12, 2) NOT NULL, ""Col14"" NUMERIC(12, 2) NOT NULL, ""Col15"" VARCHAR(128) NULL,
   ""Col17"" BOOL NOT NULL, ""Col18"" ""Flag"" NOT NULL);
--TableQuench_ShouldModifyColumnNullability
CREATE TABLE ""AlterColumnTests"".""ChangeNullability"" (""Column1"" INT NULL);
--TableQuench_ShouldHandleGoingToGeneratedColumn
CREATE TABLE ""AlterColumnTests"".""ColumnToGenerated"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
--TableQuench_ShouldHandleGoingFromGeneratedColumn
CREATE TABLE ""AlterColumnTests"".""ColumnFromGenerated"" (""Column1"" INT4 NOT NULL, ""Column2"" INT4 NOT NULL GENERATED ALWAYS AS (""Column1"" * 2) STORED);
--TableQuench_ShouldHandleChangeGenerationExpression
CREATE TABLE ""AlterColumnTests"".""ChangeGenerationExpression"" (""Column1"" INT4 NOT NULL, ""Column2"" VARCHAR(200) NOT NULL GENERATED ALWAYS AS ('OldResult') STORED);
--TableQuench_ShouldHandleChangingGeneratedColumnNotVirtual
CREATE TABLE ""AlterColumnTests"".""MakeStored"" (""Column1"" INT4 NOT NULL, ""Column2"" INT4 GENERATED ALWAYS AS (""Column1"" * 2) STORED);
--TableQuench_ShouldAlterColumnUsedInIndex
CREATE TABLE ""AlterColumnTests"".""AlterColumnInIndex"" (""Column1"" INT NOT NULL, ""Column2"" INT NULL);
CREATE INDEX ""IDX_NoDependency"" ON ""AlterColumnTests"".""AlterColumnInIndex"" (""Column1"");
CREATE INDEX ""IDX_Dependency"" ON ""AlterColumnTests"".""AlterColumnInIndex"" (""Column2"");
--TableQuench_ShouldAlterColumnUsedInIndexInclude
CREATE TABLE ""AlterColumnTests"".""AlterColumnInIndexInclude"" (""Column1"" INT NOT NULL, ""Column2"" INT);
CREATE INDEX ""IDX_NoDependency2"" ON ""AlterColumnTests"".""AlterColumnInIndexInclude"" (""Column1"");
CREATE INDEX ""IDX_Dependency2"" ON ""AlterColumnTests"".""AlterColumnInIndexInclude"" (""Column1"") INCLUDE (""Column2"");
--TableQuench_ShouldAlterColumnUsedInUniqueIndexReferencedByForeignKey
CREATE TABLE ""AlterColumnTests"".""AlterColumnInUniqueIndexInFK"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
CREATE UNIQUE INDEX ""IDX_Dependency3"" ON ""AlterColumnTests"".""AlterColumnInUniqueIndexInFK"" (""Column2"");
ALTER TABLE ""AlterColumnTests"".""AlterColumnInUniqueIndexInFK"" ADD CONSTRAINT ""FK_AlterColumnInUniqueIndexInFK_SelfRef"" FOREIGN KEY (""Column1"") REFERENCES ""AlterColumnTests"".""AlterColumnInUniqueIndexInFK"" (""Column2"");
--TableQuench_ShouldAlterColumnWithDefault
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithDefault"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL DEFAULT 0);
--TableQuench_ShouldAlterColumnWithTableLevelCheckConstraint
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithTableCheckConstraint"" (""Column1"" INT NOT NULL, ""Column2"" INT, CONSTRAINT ""CK_AlterColumnWithTableCheckConstraint_Dependency"" CHECK (""Column2"" < ""Column1""));
--TableQuench_AlterColumnWithStatistics
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithStatistics"" (""Column1"" INT NOT NULL, ""Column2"" INT);
DO $$ BEGIN IF (current_setting('server_version_num')::int / 10000) >= 14 THEN EXECUTE 'CREATE STATISTICS ""AlterColumnTests"".""ST_Dependency"" ON (""Column2"" / 1) FROM ""AlterColumnTests"".""AlterColumnWithStatistics""'; END IF; END $$;
--TableQuench_ShouldAlterColumnWithIndexFilterExpression
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithIndexFilter"" (""Column1"" INT NOT NULL, ""Column2"" INT);
CREATE INDEX ""IDX_Dependency4"" ON ""AlterColumnTests"".""AlterColumnWithIndexFilter"" (""Column1"") WHERE ""Column2"" < 100;
--TableQuench_ShouldAlterColumnWithForeignKey
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithFK"" (""Column1"" INT NOT NULL, ""Column2"" INT, CONSTRAINT ""PK_AlterColumnWithFK"" PRIMARY KEY (""Column1""));
CREATE UNIQUE INDEX ""UQ_Colum2"" ON ""AlterColumnTests"".""AlterColumnWithFK"" (""Column2"");
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithFKRef"" (""Column1"" INT NOT NULL, ""Column2"" INT, CONSTRAINT ""PK_AlterColumnWithFKRef"" PRIMARY KEY (""Column1""));
ALTER TABLE ""AlterColumnTests"".""AlterColumnWithFK"" ADD CONSTRAINT ""FK_AlterColumnWithFK_SelfRef"" FOREIGN KEY (""Column2"") REFERENCES ""AlterColumnTests"".""AlterColumnWithFK"" (""Column1"");
ALTER TABLE ""AlterColumnTests"".""AlterColumnWithFKRef"" ADD CONSTRAINT ""FK_AlterColumnWithFK_Referenced"" FOREIGN KEY (""Column2"") REFERENCES ""AlterColumnTests"".""AlterColumnWithFK"" (""Column2"");
ALTER TABLE ""AlterColumnTests"".""AlterColumnWithFK"" ADD CONSTRAINT ""FK_AlterColumnWithFK_Referencing"" FOREIGN KEY (""Column2"") REFERENCES ""AlterColumnTests"".""AlterColumnWithFKRef"" (""Column1"");
--TableQuench_AlterColumnWithGenerationExpression
CREATE TABLE ""AlterColumnTests"".""AlterColumnWithGenerated"" (""Column1"" INT4 NOT NULL, ""Column2"" INT4, ""Column3"" INT4 GENERATED ALWAYS AS (""Column2""*3) STORED);
--TableQuench_ShouldModifyColumnCollation
CREATE TABLE ""AlterColumnTests"".""ModifyColumnCollation"" (""Column1"" VARCHAR(10) COLLATE ""az-Latn-x-icu"" NULL, ""Column2"" VARCHAR(10) NULL, ""Column3"" VARCHAR(10) COLLATE ""az-Latn-x-icu"" NULL);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "AlterColumnTests",
                "Name": "ChangeType",
                "Columns": [
                    {
                      "Name": "Col1",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "CHAR(20)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col4",
                      "DataType": "VARCHAR(20)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col6",
                      "DataType": "VARCHAR",
                      "Nullable": false
                    },
                    {
                      "Name": "Col8",
                      "DataType": "VARCHAR(100)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col10",
                      "DataType": "TIMESTAMP(5)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col11",
                      "DataType": "DECIMAL(12, 3)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col12",
                      "DataType": "DECIMAL(10, 2)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col13",
                      "DataType": "NUMERIC(12, 3)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col14",
                      "DataType": "NUMERIC(10, 2)",
                      "Nullable": false
                    },
                    {
                      "Name": "Col17",
                      "DataType": "\"Flag\"",
                      "Nullable": false
                    },
                    {
                      "Name": "Col18",
                      "DataType": "BOOL",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "ChangeNullability",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "ColumnToGenerated",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT4",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "INT4",
                      "Nullable": false,
                      "Generated": "ALWAYS",
                      "GenerationExpression": "\"Column1\" * 2"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "ColumnFromGenerated",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT4",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "INT4",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "ChangeGenerationExpression",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT4",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "VARCHAR(200)",
                      "Generated": "ALWAYS",
                      "GenerationExpression": "'NewResult'"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "MakeStored",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT4",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "INT4",
                      "Generated": "ALWAYS",
                      "GenerationExpression": "\"Column1\" * 2",
                      "Virtual": false
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnInIndex",
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
                ],
                "Indexes": [
                    {
                       "Name": "IDX_Dependency",
                       "IndexColumns": "Column2"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnInIndexInclude",
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
                ],
                "Indexes": [
                    {
                       "Name": "IDX_Dependency2",
                       "IndexColumns": "Column1",
                       "IncludeColumns": "Column2"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnInUniqueIndexInFK",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                       "Name": "IDX_Dependency3",
                       "IndexColumns": "Column2",
                       "Unique": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_AlterColumnInUniqueIndexInFK_SelfRef",
                      "Columns": "Column1",
                      "RelatedTableSchema": "AlterColumnTests",
                      "RelatedTable": "AlterColumnInUniqueIndexInFK",
                      "RelatedColumns": "Column2"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnWithDefault",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "BIGINT",
                      "Nullable": false,
                      "Default": "0"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnWithTableCheckConstraint",
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
                ],
                "CheckConstraints": [
                    {
                       "Name": "CK_AlterColumnWithTableCheckConstraint_Dependency",
                       "Expression": "\"Column2\" < \"Column1\""
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnWithStatistics",
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
                ],
                "Statistics": [
                    {
                       "Name": "ST_Dependency",
                       "StatisticsColumns": "(\"Column2\" / 1)"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnWithIndexFilter",
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
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnWithFK",
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
                "Schema": "AlterColumnTests",
                "Name": "AlterColumnWithGenerated",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT4",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "INT8",
                      "Nullable": false
                    },
                    {
                      "Name": "Column3",
                      "DataType": "INT8",
                      "Generated": "ALWAYS",
                      "GenerationExpression": "\"Column2\"*3"
                    }
                ]
            },
            {
                "Schema": "AlterColumnTests",
                "Name": "ModifyColumnCollation",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": "default"
                    },
                    {
                      "Name": "Column2",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": "bs-Latn-x-icu"
                    },
                    {
                      "Name": "Column3",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
    // An array column reports data_type=ARRAY / udt_name=_text in the catalog while the package declares
    // text[]. The two were never reconciled, so the column compared unequal to itself and was re-modified on
    // EVERY deploy -- for any PostgreSQL array column, not one type. An idempotency break like this exits 0
    // and logs "Successfully Quenched", so nothing surfaces it; the only signal is the same object changing
    // again on a re-run, which is what this asserts.
    [Test]
    public void AlterColumn_ArrayColumns_AreIdempotentAcrossRepeatedDeploys()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var table = "arr_idempotent_" + Guid.NewGuid().ToString("N")[..8];
        var json = $@"[
            {{
                ""Schema"": ""public"",
                ""Name"": ""{table}"",
                ""Columns"": [
                    {{ ""Name"": ""Id"", ""DataType"": ""integer"", ""Nullable"": false }},
                    {{ ""Name"": ""Counts"", ""DataType"": ""int4[]"", ""Nullable"": true }},
                    {{ ""Name"": ""Tags"", ""DataType"": ""text[]"", ""Nullable"": true }},
                    {{ ""Name"": ""Legacy"", ""DataType"": ""_text"", ""Nullable"": true }},
                    {{ ""Name"": ""Sizes"", ""DataType"": ""integer[]"", ""Nullable"": true }},
                    {{ ""Name"": ""Codes"", ""DataType"": ""varchar(20)[]"", ""Nullable"": true }},
                    {{ ""Name"": ""Amounts"", ""DataType"": ""numeric(10,2)[]"", ""Nullable"": true }}
                ]
            }}
        ]";

        try
        {
            RunTableQuenchProc(cmd, json);   // creates

            // Two further isolated passes: the catalog now matches the declaration exactly, so neither may
            // touch the columns again.
            for (var pass = 2; pass <= 3; pass++)
            {
                cmd.CommandText = "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()";
                cmd.ExecuteNonQuery();

                RunTableQuenchProc(cmd, json);

                cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                                      WHERE ""SessionId"" = pg_backend_pid()
                                        AND ""ObjectName"" LIKE '%{table}%'";
                var changes = Convert.ToInt32(cmd.ExecuteScalar());
                if (changes != 0)
                {
                    cmd.CommandText = $@"SELECT string_agg(""ObjectType"" || '/' || ""ActionType"" || ' ' || ""ObjectName"", '; ')
                                          FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""SessionId"" = pg_backend_pid()";
                    Assert.Fail($"pass {pass}: expected no changes, got {changes}: {cmd.ExecuteScalar()}");
                }
            }
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.\"{table}\"";
            cmd.ExecuteNonQuery();
        }
    }
    // The catalog records an identity column's KIND and nothing else, but a package may legitimately declare
    // the sequence options too -- GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1). Comparing the
    // declared string verbatim re-modified every identity column on every deploy (10 of them in one shipped
    // demo). The create path still emits the options; they just take no part in the comparison, because
    // SchemaSmith does not manage them declaratively.
    [Test]
    public void AlterColumn_IdentityWithSequenceOptions_IsIdempotentAcrossRepeatedDeploys()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var table = "ident_idem_" + Guid.NewGuid().ToString("N")[..8];
        var json = $@"[
            {{
                ""Schema"": ""public"",
                ""Name"": ""{table}"",
                ""Columns"": [
                    {{ ""Name"": ""Id"", ""DataType"": ""integer"", ""Nullable"": false,
                       ""Generated"": ""GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1)"" }},
                    {{ ""Name"": ""ByDefault"", ""DataType"": ""integer"", ""Nullable"": false,
                       ""Generated"": ""GENERATED BY DEFAULT AS IDENTITY(START WITH 5 INCREMENT BY 2)"" }},
                    {{ ""Name"": ""Plain"", ""DataType"": ""integer"", ""Nullable"": true }}
                ]
            }}
        ]";

        try
        {
            RunTableQuenchProc(cmd, json);

            for (var pass = 2; pass <= 3; pass++)
            {
                cmd.CommandText = "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()";
                cmd.ExecuteNonQuery();

                RunTableQuenchProc(cmd, json);

                cmd.CommandText = $@"SELECT COALESCE(STRING_AGG(""ObjectType"" || '/' || ""ActionType"" || ' ' || ""ObjectName"", '; '), '')
                                       FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""SessionId"" = pg_backend_pid()
                                        AND ""ObjectName"" LIKE '%{table}%'";
                Assert.That(Convert.ToString(cmd.ExecuteScalar()), Is.Empty,
                    $"pass {pass}: an identity column that already matches must not be re-modified");
            }

            // The declared options must still have reached the engine on creation.
            cmd.CommandText = $@"SELECT seqstart FROM pg_sequence s
                                   JOIN pg_class c ON c.oid = s.seqrelid
                                  WHERE c.relname = '{table}_ByDefault_seq'";
            var start = cmd.ExecuteScalar();
            Assert.That(start, Is.Not.Null.And.EqualTo(5L),
                "ignoring the options for COMPARISON must not stop the create path from applying them");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.\"{table}\" CASCADE";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }


}
