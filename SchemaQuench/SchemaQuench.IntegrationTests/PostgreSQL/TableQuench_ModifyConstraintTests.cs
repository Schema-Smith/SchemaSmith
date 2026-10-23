// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ModifyConstraintTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldModifyDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT \"SchemaSmith\".\"StripParenWrapping\"(column_default) FROM information_schema.columns WHERE table_schema = 'ModifyConstraintTests' AND table_name = 'ModifyMyDefault' AND column_name = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT  ""SchemaSmith"".""StripParenWrapping""(SUBSTRING(pg_catalog.PG_GET_CONSTRAINTDEF(con.oid) FROM 6)) 
                              FROM pg_catalog.pg_constraint con 
                              WHERE con.conname = 'CHK_ModifyMyTableCheck_MyCheck'
                                AND con.contype = 'c';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("\"Id\" < \"Col2\""));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForColumnChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
                                      FROM pg_attribute a
                                      CROSS JOIN LATERAL UNNEST(con.conkey) WITH ORDINALITY AS u(element, idx)
                                      WHERE a.attrelid = con.conrelid
                                        AND a.attnum = element)
                              FROM pg_catalog.pg_constraint con
                              JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
                              JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                              WHERE con.conname = 'FK_ModifyFKColumn_ModifyFKColumnRef'
                                AND con.contype = 'f';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col2"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForReferenceColumnChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
                                      FROM pg_attribute a
                                      CROSS JOIN LATERAL UNNEST(con.confkey) WITH ORDINALITY AS u(element, idx)
                                      WHERE a.attrelid = frel.oid
                                        AND a.attnum = element)
                              FROM pg_catalog.pg_constraint con
                              JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
                              JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                              WHERE con.conname = 'FK_ModifyFKReferenceColumn_ModifyFKReferenceColumnRef'
                                AND con.contype = 'f';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("RefCol"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForReferenceTableChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT frel.relname
                              FROM pg_catalog.pg_constraint con
                              JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
                              JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                              WHERE con.conname = 'FK_ModifyFKReferenceTable_ModifyFKReferenceTableRef'
                                AND con.contype = 'f';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ModifyFKReferenceTableRefNew"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForCascadeDeleteChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT CASE con.confdeltype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' END
                              FROM pg_catalog.pg_constraint con
                              JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
                              JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                              WHERE con.conname = 'FK_ModifyFKCascadeDelete_ModifyFKCascadeDeleteRef'
                                AND con.contype = 'f';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(""));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForCascadeUpdateChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT CASE con.confupdtype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' END
                              FROM pg_catalog.pg_constraint con
                              JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
                              JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                              WHERE con.conname = 'FK_ModifyFKCascadeUpdate_ModifyFKCascadeUpdateRef'
                                AND con.contype = 'f';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(""));
        conn.Close();
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE SCHEMA ""ModifyConstraintTests"";
--TableQuench_ShouldModifyDefault
CREATE TABLE ""ModifyConstraintTests"".""ModifyMyDefault"" (""Id"" INT NOT NULL DEFAULT 10);
--TableQuench_ShouldModifyTableLevelCheckConstraint
CREATE TABLE ""ModifyConstraintTests"".""ModifyMyTableCheck"" (""Id"" INT NOT NULL, ""Col2"" INT);
ALTER TABLE ""ModifyConstraintTests"".""ModifyMyTableCheck"" ADD CONSTRAINT ""CHK_ModifyMyTableCheck_MyCheck"" CHECK (""Col2"">""Id"");
--TableQuench_ShouldModifyForeignKeyForColumnChange
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKColumn"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT, ""Col3"" INT);
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKColumnRef"" (""Id"" INT NOT NULL PRIMARY KEY);
ALTER TABLE ""ModifyConstraintTests"".""ModifyFKColumn"" ADD CONSTRAINT ""FK_ModifyFKColumn_ModifyFKColumnRef"" FOREIGN KEY (""Col3"") REFERENCES ""ModifyConstraintTests"".""ModifyFKColumnRef"" (""Id"");
--TableQuench_ShouldModifyForeignKeyForReferenceColumnChange
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKReferenceColumn"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT, ""Col3"" INT);
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKReferenceColumnRef"" (""Id"" INT NOT NULL PRIMARY KEY, ""RefCol"" INT NOT NULL);
CREATE UNIQUE INDEX ""IDX_RefKey"" ON ""ModifyConstraintTests"".""ModifyFKReferenceColumnRef"" (""RefCol"");
ALTER TABLE ""ModifyConstraintTests"".""ModifyFKReferenceColumn"" ADD CONSTRAINT ""FK_ModifyFKReferenceColumn_ModifyFKReferenceColumnRef"" FOREIGN KEY (""Col3"") REFERENCES ""ModifyConstraintTests"".""ModifyFKReferenceColumnRef"" (""Id"");
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKReferenceTable"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT, ""Col3"" INT);
--TableQuench_ShouldModifyForeignKeyForReferenceTableChange
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKReferenceTableRef"" (""Id"" INT NOT NULL PRIMARY KEY);
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKReferenceTableRefNew"" (""Id"" INT NOT NULL PRIMARY KEY);
ALTER TABLE ""ModifyConstraintTests"".""ModifyFKReferenceTable"" ADD CONSTRAINT ""FK_ModifyFKReferenceTable_ModifyFKReferenceTableRef"" FOREIGN KEY (""Col3"") REFERENCES ""ModifyConstraintTests"".""ModifyFKReferenceTableRef"" (""Id"");
--TableQuench_ShouldModifyForeignKeyForCascadeDeleteChange
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKCascadeDelete"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT, ""Col3"" INT);
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKCascadeDeleteRef"" (""Id"" INT NOT NULL PRIMARY KEY);
ALTER TABLE ""ModifyConstraintTests"".""ModifyFKCascadeDelete"" ADD CONSTRAINT ""FK_ModifyFKCascadeDelete_ModifyFKCascadeDeleteRef"" FOREIGN KEY (""Col3"") REFERENCES ""ModifyConstraintTests"".""ModifyFKCascadeDeleteRef"" (""Id"") ON DELETE CASCADE;
--TableQuench_ShouldModifyForeignKeyForCascadeUpdateChange
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKCascadeUpdate"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT, ""Col3"" INT);
CREATE TABLE ""ModifyConstraintTests"".""ModifyFKCascadeUpdateRef"" (""Id"" INT NOT NULL PRIMARY KEY);
ALTER TABLE ""ModifyConstraintTests"".""ModifyFKCascadeUpdate"" ADD CONSTRAINT ""FK_ModifyFKCascadeUpdate_ModifyFKCascadeUpdateRef"" FOREIGN KEY (""Col3"") REFERENCES ""ModifyConstraintTests"".""ModifyFKCascadeUpdateRef"" (""Id"") ON UPDATE CASCADE;
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyMyDefault",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false,
                      "Default": "0"
                    }
                ]
            },
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyMyTableCheck",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_ModifyMyTableCheck_MyCheck",
                      "Expression": "\"Id\"<\"Col2\""
                    }
                ]
            },
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyFKColumn",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "Col3",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_ModifyFKColumn_ModifyFKColumnRef",
                      "Columns": "Col2",
                      "RelatedTableSchema": "ModifyConstraintTests",
                      "RelatedTable": "ModifyFKColumnRef",
                      "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyFKReferenceColumn",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "Col3",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_ModifyFKReferenceColumn_ModifyFKReferenceColumnRef",
                      "Columns": "Col3",
                      "RelatedTableSchema": "ModifyConstraintTests",
                      "RelatedTable": "ModifyFKReferenceColumnRef",
                      "RelatedColumns": "RefCol"
                    }
                ]
            },
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyFKReferenceTable",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "Col3",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_ModifyFKReferenceTable_ModifyFKReferenceTableRef",
                      "Columns": "Col3",
                      "RelatedTableSchema": "ModifyConstraintTests",
                      "RelatedTable": "ModifyFKReferenceTableRefNew",
                      "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyFKCascadeDelete",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "Col3",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_ModifyFKCascadeDelete_ModifyFKCascadeDeleteRef",
                      "Columns": "Col2",
                      "RelatedTableSchema": "ModifyConstraintTests",
                      "RelatedTable": "ModifyFKCascadeDeleteRef",
                      "RelatedColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "ModifyConstraintTests",
                "Name": "ModifyFKCascadeUpdate",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Col2",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "Col3",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "FK_ModifyFKCascadeUpdate_ModifyFKCascadeUpdateRef",
                      "Columns": "Col2",
                      "RelatedTableSchema": "ModifyConstraintTests",
                      "RelatedTable": "ModifyFKCascadeUpdateRef",
                      "RelatedColumns": "Id"
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
