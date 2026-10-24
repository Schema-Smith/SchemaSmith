// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ModifyIndexTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldModifyIndexWhenFilterExpressionChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT ""SchemaSmith"".""StripParenWrapping""(PG_GET_EXPR(idx.indpred, idx.indrelid))
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_Filtered';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("\"Column1\" > 50"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT (SELECT STRING_AGG(a.attname || CASE WHEN (idx.indoption[idx] & 1) = 1 THEN ' DESC' ELSE '' END, ',' ORDER BY idx)
                                      FROM pg_attribute a
                                      CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                      WHERE a.attrelid = idx.indrelid
                                        AND idx <= idx.indnkeyatts
                                        AND a.attnum = element)
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_ColumnList';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListChangesIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT (SELECT STRING_AGG(a.attname || CASE WHEN (idx.indoption[idx] & 1) = 1 THEN ' DESC' ELSE '' END, ',' ORDER BY idx)
                                      FROM pg_attribute a
                                      CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                      WHERE a.attrelid = idx.indrelid
                                        AND idx <= idx.indnkeyatts
                                        AND a.attnum = element)
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_ColumnListIO';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenSortColumnListOrderChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT (SELECT STRING_AGG(a.attname || CASE WHEN (idx.indoption[idx] & 1) = 1 THEN ' DESC' ELSE '' END, ',' ORDER BY idx)
                                      FROM pg_attribute a
                                      CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                      WHERE a.attrelid = idx.indrelid
                                        AND idx <= idx.indnkeyatts
                                        AND a.attnum = element)
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_ColumnList2';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column2,Column1"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenIncludeListChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
                                      FROM pg_attribute a
                                      CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                      WHERE a.attrelid = idx.indrelid
                                        AND idx > idx.indnkeyatts
                                        AND a.attnum = element)
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_IncludeList';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column3"));

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesUnique()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT idx.indisunique
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_BecomesUnique';";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexWhenItBecomesNonUnique()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT idx.indisunique
                              FROM pg_index idx
                              JOIN pg_class i ON i.oid = idx.indexrelid
                              LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u')
                              WHERE i.relname = 'IDX_BecomesNonUnique';";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

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
CREATE SCHEMA ""ModifyIndexTests"";
--ShouldModifyIndexWhenFilterExpressionChanges
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexFilterExpression"" (""Column1"" INT NOT NULL);
CREATE INDEX ""IDX_Filtered"" ON ""ModifyIndexTests"".""ModifyIndexFilterExpression"" (""Column1"") WHERE ""Column1"">(20);
--ShouldModifyIndexWhenSortColumnListChanges
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexColumnList"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
CREATE INDEX ""IDX_ColumnList"" ON ""ModifyIndexTests"".""ModifyIndexColumnList"" (""Column1"");
--ShouldModifyIndexWhenSortColumnListOrderChanges
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexColumnListOrder"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
CREATE INDEX ""IDX_ColumnList2"" ON ""ModifyIndexTests"".""ModifyIndexColumnListOrder"" (""Column1"", ""Column2"");
--ShouldModifyIndexWhenIncludeListChanges
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexIncludeList"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL, ""Column3"" INT NOT NULL);
CREATE INDEX ""IDX_IncludeList"" ON ""ModifyIndexTests"".""ModifyIndexIncludeList"" (""Column1"") INCLUDE (""Column2"");
--ShouldModifyIndexWhenItBecomesUnique
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexBecomesUnique"" (""Column1"" INT NOT NULL);
--ShouldModifyIndexWhenItBecomesNonUnique
CREATE INDEX ""IDX_BecomesUnique"" ON ""ModifyIndexTests"".""ModifyIndexBecomesUnique"" (""Column1"");
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexBecomesNonUnique"" (""Column1"" INT NOT NULL, ""Column2"" VARCHAR(200) NULL, ""Column3"" INT NOT NULL);
CREATE UNIQUE INDEX ""IDX_BecomesNonUnique"" ON ""ModifyIndexTests"".""ModifyIndexBecomesNonUnique"" (""Column1"");
ALTER TABLE ""ModifyIndexTests"".""ModifyIndexBecomesNonUnique"" ADD CONSTRAINT ""FK_ModifyIndexBecomesNonUnique_SelfRef"" FOREIGN KEY (""Column3"") REFERENCES ""ModifyIndexTests"".""ModifyIndexBecomesNonUnique"" (""Column1"");

-- Index Only
--ShouldModifyIndexWhenSortColumnListChangesIndexOnly
CREATE TABLE ""ModifyIndexTests"".""ModifyIndexColumnListIO"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
CREATE INDEX ""IDX_ColumnListIO"" ON ""ModifyIndexTests"".""ModifyIndexColumnListIO"" (""Column1"");
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexFilterExpression",
                "Columns": [
                    {
                        "Name": "Column1",
                        "DataType": "INT",
                        "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                        "Name": "IDX_Filtered",
                        "IndexColumns": "Column1",
                        "FilterExpression": "\"Column1\">(50)"
                    }
                ]
            },
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexColumnList",
                "Columns": [
                    {
                    "Name": "Column1",
                    "DataType": "INT",
                    "Nullable": false
                    },
                    {
                    "Name": "Column2",
                    "DataType": "INT",
                    "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                    "Name": "IDX_ColumnList",
                    "IndexColumns": "Column2"
                    }
                ]
            },
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexColumnListOrder",
                "Columns": [
                    {
                        "Name": "Column1",
                        "DataType": "INT",
                        "Nullable": false
                    },
                    {
                        "Name": "Column2",
                        "DataType": "INT",
                        "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                        "Name": "IDX_ColumnList2",
                        "IndexColumns": "Column2,Column1"
                    }
                ]
            },
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexIncludeList",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column3",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_IncludeList",
                      "IndexColumns": "Column1",
                      "IncludeColumns": "Column3"
                    }
                ]
            },
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexBecomesUnique",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_BecomesUnique",
                      "IndexColumns": "Column1",
                      "Unique": true
                    }
                ]
            },
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexBecomesNonUnique",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column2",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_BecomesNonUnique",
                      "IndexColumns": "Column1",
                      "Unique": false
                    },
                    {
                      "Name": "IDX_NewUnique",
                      "IndexColumns": "Column1",
                      "Unique": true
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);
        conn.Close();

        conn.Open();
        // Index Only
        json = """
        [
            {
                "Schema": "ModifyIndexTests",
                "Name": "ModifyIndexColumnListIO",
                "Indexes": [
                    {
                      "Name": "IDX_ColumnListIO",
                      "IndexColumns": "Column2"
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json, indexOnly: true);

        conn.Close();
    }
}
