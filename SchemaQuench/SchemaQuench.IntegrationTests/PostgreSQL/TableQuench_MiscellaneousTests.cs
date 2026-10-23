// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using System;
using Npgsql;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_MiscellaneousTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldRenameIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'RenameMyIndex' AND indexname = 'IDX_RightName')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'RenameMyIndex' AND indexname = 'IDX_WrongName')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRenameIndexIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'RenameMyIndexIO' AND indexname = 'IDX_RightNameIO')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'RenameMyIndexIO' AND indexname = 'IDX_WrongNameIO')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRenameUniqueConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'RenameMyUniqueConstraint' AND indexname = 'UQ_NewName')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'RenameMyUniqueConstraint' AND indexname = 'UQ_OldName')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_DropFK_SelfRef' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_DropFK_SelfRef2' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldModifyStatisticsWhenColumnListChanges()
    {
        if (PgServerMajor() < 14) Assert.Ignore("Expression statistics (CREATE STATISTICS on an expression) require PostgreSQL 14+.");
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"SELECT 
                               COALESCE(ARRAY_TO_STRING(ARRAY_CAT(COALESCE((SELECT ARRAY_AGG(a.attname)
                                                                              FROM UNNEST(se.stxkeys) WITH ORDINALITY AS t(attnum, ord)
                                                                              JOIN pg_attribute a ON a.attrelid = se.stxrelid AND a.attnum = t.attnum
                                                                              WHERE a.attnum > 0),
                                                                           ARRAY[]::text[]),
                                                                  COALESCE((SELECT ARRAY_AGG(exp.expr)
                                                                              FROM pg_stats_ext_exprs exp
                                                                              WHERE exp.schemaname = 'MiscellaneousTests' AND exp.statistics_name = se.stxname),
                                                                           ARRAY[]::text[])), ','), '') AS ""StatisticsColumns""
                          FROM pg_statistic_ext se
                          WHERE se.stxrelid = ('""MiscellaneousTests"".""ModifyStatisticsColumnList""')::regclass
                            AND se.stxname = 'ST_ColumnList'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("(\"Column2\" / 1)"));

        conn.Close();
    }

    [Test]
    public void ShouldErrorWhenUpdatingWrongProductTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        var json = """
            {
                "Schema": "MiscellaneousTests",
                "Name": "TableOwnedByOtherProduct",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            }
            """;
        var ex = Assert.Throws<PostgresException>(() => RunTableQuenchProc(cmd, json));
        Assert.That(ex!.Message, Contains.Substring("One or more tables in this quench are already owned by another product"));

        conn.Close();
    }

    [Test]
    public void ShouldDropIndexNoLongerPartOfProduct()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'IndexNoLongerInProduct' AND indexname = 'IDX_DropMe')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'IndexNoLongerInProduct' AND indexname = 'IDX_Custom')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldDropIndexNoLongerPartOfProductIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'MiscellaneousTests' AND tablename = 'IndexNoLongerInProductIO' AND indexname = 'IDX_DropMeIO')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        conn.Close();
    }

    [Test]
    public void ShouldDropTableNoLongerPartOfProduct()
    {
        var productName = Guid.NewGuid().ToString();
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE TABLE ""MiscellaneousTests"".""TableNoLongerInProduct"" (""Column1"" INT NOT NULL);
INSERT INTO ""SchemaSmith"".""ProductOwnership"" (""Schema"", ""TableName"", ""IndexName"", ""ProductName"") VALUES('MiscellaneousTests', 'TableNoLongerInProduct', NULL, '{productName}');
";
        cmd.ExecuteNonQuery();

        var json = """
            {
                "Schema": "MiscellaneousTests",
                "Name": "TableInProduct",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            }
            """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true);";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = 'MiscellaneousTests' AND table_name = 'TableNoLongerInProduct')";
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
        cmd.CommandText = @$"
CREATE SCHEMA ""MiscellaneousTests"";
--TableQuench_ShouldRenameIndex
CREATE TABLE ""MiscellaneousTests"".""RenameMyIndex"" (""Id"" INT NOT NULL);
CREATE INDEX ""IDX_WrongName"" ON ""MiscellaneousTests"".""RenameMyIndex"" (""Id"");
--TableQuench_ShouldRenameUniqueConstraint
CREATE TABLE ""MiscellaneousTests"".""RenameMyUniqueConstraint"" (""Id"" INT NOT NULL);
ALTER TABLE ""MiscellaneousTests"".""RenameMyUniqueConstraint"" ADD CONSTRAINT ""UQ_OldName"" UNIQUE (""Id"");
--TableQuench_ShouldHandleRemovingForeignKey
CREATE TABLE ""MiscellaneousTests"".""DropFK"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL, CONSTRAINT ""PK_DropFK"" PRIMARY KEY (""Column1""));
CREATE UNIQUE INDEX ""UQ_Colum2"" ON ""MiscellaneousTests"".""DropFK"" (""Column2"");
ALTER TABLE ""MiscellaneousTests"".""DropFK"" ADD CONSTRAINT ""FK_DropFK_SelfRef"" FOREIGN KEY (""Column2"") REFERENCES ""MiscellaneousTests"".""DropFK"" (""Column1"");
ALTER TABLE ""MiscellaneousTests"".""DropFK"" ADD CONSTRAINT ""FK_DropFK_SelfRef2"" FOREIGN KEY (""Column2"") REFERENCES ""MiscellaneousTests"".""DropFK"" (""Column1"");
--ShouldModifyStatisticsWhenColumnListChanges
CREATE TABLE ""MiscellaneousTests"".""ModifyStatisticsColumnList"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
DO $$ BEGIN IF (current_setting('server_version_num')::int / 10000) >= 14 THEN EXECUTE 'CREATE STATISTICS ""MiscellaneousTests"".""ST_ColumnList"" ON (""Column1"" / 1) FROM ""MiscellaneousTests"".""ModifyStatisticsColumnList""'; END IF; END $$;
--ShouldDropIndexNoLongerPartOfProduct
CREATE TABLE ""MiscellaneousTests"".""IndexNoLongerInProduct"" (""Column1"" INT NOT NULL);
CREATE INDEX ""IDX_DropMe"" ON ""MiscellaneousTests"".""IndexNoLongerInProduct"" (""Column1"");
CREATE INDEX ""IDX_Custom"" ON ""MiscellaneousTests"".""IndexNoLongerInProduct"" (""Column1"");
INSERT INTO ""SchemaSmith"".""ProductOwnership"" (""Schema"", ""TableName"", ""IndexName"", ""ProductName"") VALUES('MiscellaneousTests', 'IndexNoLongerInProduct', NULL, '{_productName}');
INSERT INTO ""SchemaSmith"".""ProductOwnership"" (""Schema"", ""TableName"", ""IndexName"", ""ProductName"") VALUES('MiscellaneousTests', 'IndexNoLongerInProduct', 'IDX_DropMe', '{_productName}');

-- Index Only
--TableQuench_ShouldRenameIndexIndexOnly
CREATE TABLE ""MiscellaneousTests"".""RenameMyIndexIO"" (""Id"" INT NOT NULL);
CREATE INDEX ""IDX_WrongNameIO"" ON ""MiscellaneousTests"".""RenameMyIndexIO"" (""Id"");
--ShouldDropIndexNoLongerPartOfProductIndexOnly
CREATE TABLE ""MiscellaneousTests"".""IndexNoLongerInProductIO"" (""Column1"" INT NOT NULL);
CREATE INDEX ""IDX_DropMeIO"" ON ""MiscellaneousTests"".""IndexNoLongerInProductIO"" (""Column1"");

-- Exception Cases
CREATE TABLE ""MiscellaneousTests"".""TableOwnedByOtherProduct"" (""Column1"" INT NOT NULL);
INSERT INTO ""SchemaSmith"".""ProductOwnership"" (""Schema"", ""TableName"", ""IndexName"", ""ProductName"") VALUES('MiscellaneousTests', 'TableOwnedByOtherProduct', NULL, 'OtherProduct');
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "MiscellaneousTests",
                "Name": "RenameMyIndex",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_RightName",
                      "IndexColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "MiscellaneousTests",
                "Name": "RenameMyUniqueConstraint",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "UQ_NewName",
                      "IndexColumns": "Id",
                      "Unique": true,
                      "UniqueConstraint": true
                    }
                ]
            },
            {
                "Schema": "MiscellaneousTests",
                "Name": "DropFK",
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
                "ForeignKeys": [
                    {
                      "Name": "FK_DropFK_SelfRef2",
                      "Columns": "Column2",
                      "RelatedTableSchema": "MiscellaneousTests",
                      "RelatedTable": "DropFK",
                      "RelatedColumns": "Column1"
                    }
                ]
            },
            {
                "Schema": "MiscellaneousTests",
                "Name": "DropConflictingClusteredIdx",
                "Columns": [
                    {
                      "Name": "Id",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_NewClusteredIdx",
                      "IndexColumns": "Id",
                      "Clustered": true
                    }
                ]
            },
            {
                "Schema": "MiscellaneousTests",
                "Name": "ModifyStatisticsColumnList",
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
                "Statistics": [
                    {
                      "Name": "ST_ColumnList",
                      "StatisticsColumns": "(\"Column2\" / 1)"
                    }
                ]
            },
            {
                "Schema": "MiscellaneousTests",
                "Name": "IndexNoLongerInProduct",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
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
                "Schema": "MiscellaneousTests",
                "Name": "RenameMyIndexIO",
                "Indexes": [
                    {
                      "Name": "IDX_RightNameIO",
                      "IndexColumns": "Id"
                    }
                ]
            },
            {
                "Schema": "MiscellaneousTests",
                "Name": "IndexNoLongerInProductIO"
            }
            ]
            """;
        RunTableQuenchProc(cmd, json, indexOnly: true);
        conn.Close();
    }

    [Test]
    public void ShouldRenameTableViaOldName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var oldTableName = $"old_table_{uniqueId}";
        var newTableName = $"new_table_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE public.{oldTableName} (id INT NOT NULL, val INT NOT NULL);
INSERT INTO public.{oldTableName} (id, val) VALUES (1, 42);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{newTableName}}",
            "OldName": "{{oldTableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "val", "DataType": "INT4", "Nullable": false}
            ]
        }
        """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false)";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{oldTableName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old table should no longer exist");

        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{newTableName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "New table should exist");

        cmd.CommandText = $"SELECT val FROM public.{newTableName} WHERE id = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(42), "Data should be preserved after rename");

        conn.Close();
    }

    [Test]
    public void ShouldRenameColumnViaOldName()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"col_rename_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE public.{tableName} (id INT NOT NULL, old_col INT NOT NULL);
INSERT INTO public.{tableName} (id, old_col) VALUES (1, 99);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "new_col", "OldName": "old_col", "DataType": "INT4", "Nullable": false}
            ]
        }
        """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false)";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{tableName}' AND column_name = 'old_col'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old column name should no longer exist");

        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{tableName}' AND column_name = 'new_col'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "New column name should exist");

        cmd.CommandText = $"SELECT new_col FROM public.{tableName} WHERE id = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(99), "Data should be preserved after column rename");

        conn.Close();
    }

    // Two-deploy declarative rename: v1 creates + owns the table under its old name; v2 renames it
    // via OldName (WITH autodrop on, so the removed-drop pass runs against the stale old-name
    // ownership row). Guards the vanilla (non-recyclebin) path — the committed single-deploy rename
    // tests never seeded ownership, so they never exercised deploy-over-a-prior-state.
    [Test]
    public void ShouldRenameTableViaOldNameAcrossTwoDeploys()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var oldTableName = $"two_old_tbl_{uniqueId}";
        var newTableName = $"two_new_tbl_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // v1 deploy: create + own the table under its old name, then add data.
        var jsonV1 = $$"""
        {
            "Schema": "public",
            "Name": "{{oldTableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "val", "DataType": "INT4", "Nullable": false}
            ]
        }
        """;
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{jsonV1.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"INSERT INTO public.{oldTableName} (id, val) VALUES (1, 42)";
        cmd.ExecuteNonQuery();

        // v2 deploy: rename via OldName, autodrop on.
        var jsonV2 = $$"""
        {
            "Schema": "public",
            "Name": "{{newTableName}}",
            "OldName": "{{oldTableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "val", "DataType": "INT4", "Nullable": false}
            ]
        }
        """;
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{jsonV2.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{oldTableName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old table should no longer exist");
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{newTableName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "New table should exist");
        cmd.CommandText = $"SELECT val FROM public.{newTableName} WHERE id = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(42), "Data should be preserved after rename");

        conn.Close();
    }

    // Two-deploy declarative column rename: v1 creates + owns the table; v2 renames a column via
    // its OldName (autodrop on). Guards the column-rename path across a prior-state deploy.
    [Test]
    public void ShouldRenameColumnViaOldNameAcrossTwoDeploys()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"two_col_rn_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var jsonV1 = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "old_col", "DataType": "INT4", "Nullable": false}
            ]
        }
        """;
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{jsonV1.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"INSERT INTO public.{tableName} (id, old_col) VALUES (1, 99)";
        cmd.ExecuteNonQuery();

        var jsonV2 = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "new_col", "OldName": "old_col", "DataType": "INT4", "Nullable": false}
            ]
        }
        """;
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{jsonV2.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{tableName}' AND column_name = 'old_col'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old column name should no longer exist");
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{tableName}' AND column_name = 'new_col'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "New column name should exist");
        cmd.CommandText = $"SELECT new_col FROM public.{tableName} WHERE id = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(99), "Data should be preserved after column rename");

        conn.Close();
    }

    // Two-deploy declarative rename where the renamed table ALSO renames its own PK and unique
    // constraint (new names on the renamed table). PG regression for 42P16: the carried-over
    // old-named PK's ownership row still points at the old table name, so the removed-from-product
    // drop pass (keyed on the new table name) never drops it, and the add pass then issues a second
    // ADD PRIMARY KEY -> "multiple primary keys for table not allowed". FKs are covered too (they
    // drop structurally, by absence, so they already converge — asserted here to lock that in).
    [Test]
    public void ShouldRenameTableAndItsConstraintsViaOldNameAcrossTwoDeploys()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var oldTableName = $"cr_old_{uniqueId}";
        var newTableName = $"cr_new_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var parentTable = $"cr_cust_{uniqueId}";

        // v1 deploy: mirror the C3 M5 capstone shape that surfaced this — a GENERATED identity PK
        // column (mixed-case identifiers) plus a cross-table FK. Create + own under the old name.
        var jsonV1 = $$"""
        [
            {
                "Schema": "public",
                "Name": "{{parentTable}}",
                "Columns": [
                    {"Name": "CustomerId", "DataType": "INTEGER", "Nullable": false}
                ],
                "Indexes": [
                    {"Name": "pk_{{parentTable}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "CustomerId"}
                ]
            },
            {
                "Schema": "public",
                "Name": "{{oldTableName}}",
                "Columns": [
                    {"Name": "OrderId", "DataType": "INTEGER", "Generated": "GENERATED BY DEFAULT AS IDENTITY"},
                    {"Name": "CustomerId", "DataType": "INTEGER"}
                ],
                "Indexes": [
                    {"Name": "pk_{{oldTableName}}", "PrimaryKey": true, "IndexColumns": "OrderId"}
                ],
                "ForeignKeys": [
                    {"Name": "fk_{{oldTableName}}_customer", "Columns": "CustomerId", "RelatedTableSchema": "public", "RelatedTable": "{{parentTable}}", "RelatedColumns": "CustomerId"}
                ]
            }
        ]
        """;
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{jsonV1.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"INSERT INTO public.{parentTable} (\"CustomerId\") VALUES (7)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO public.{oldTableName} (\"OrderId\", \"CustomerId\") VALUES (1, 7)";
        cmd.ExecuteNonQuery();

        // v2 deploy: rename the table via OldName AND rename its PK and FK (the first-instinct
        // capstone authoring — new names on the renamed table).
        var jsonV2 = $$"""
        [
            {
                "Schema": "public",
                "Name": "{{parentTable}}",
                "Columns": [
                    {"Name": "CustomerId", "DataType": "INTEGER", "Nullable": false}
                ],
                "Indexes": [
                    {"Name": "pk_{{parentTable}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "CustomerId"}
                ]
            },
            {
                "Schema": "public",
                "Name": "{{newTableName}}",
                "OldName": "{{oldTableName}}",
                "Columns": [
                    {"Name": "OrderId", "DataType": "INTEGER", "Generated": "GENERATED BY DEFAULT AS IDENTITY"},
                    {"Name": "CustomerId", "DataType": "INTEGER"}
                ],
                "Indexes": [
                    {"Name": "pk_{{newTableName}}", "PrimaryKey": true, "IndexColumns": "OrderId"}
                ],
                "ForeignKeys": [
                    {"Name": "fk_{{newTableName}}_customer", "Columns": "CustomerId", "RelatedTableSchema": "public", "RelatedTable": "{{parentTable}}", "RelatedColumns": "CustomerId"}
                ]
            }
        ]
        """;
        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{jsonV2.Replace("'", "''")}', p_DropTablesRemovedFromProduct := true, p_DropUnknownIndexes := false)";
        Assert.DoesNotThrow(() => TransientRetry.Run(() => cmd.ExecuteNonQuery()), "Renaming a table and its constraints via OldName should not error (PG 42P16)");

        // Table renamed, data preserved.
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{oldTableName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old table should no longer exist");
        cmd.CommandText = $"SELECT \"CustomerId\" FROM public.{newTableName} WHERE \"OrderId\" = 1";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(7), "Data should be preserved after rename");

        // Exactly one primary key, under the new name; the old-named PK is gone.
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_constraint c JOIN pg_class t ON c.conrelid = t.oid WHERE t.relname = '{newTableName}' AND c.contype = 'p'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Renamed table should have exactly one primary key");
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_constraint WHERE conname = 'pk_{newTableName}' AND contype = 'p'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "PK should exist under its new name");
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_constraint WHERE conname = 'pk_{oldTableName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old-named PK should be gone");

        // FK renamed likewise (FKs drop structurally/by-absence, so this already converges — lock it in).
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_constraint WHERE conname = 'fk_{newTableName}_customer' AND contype = 'f'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "FK should exist under its new name");
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_constraint WHERE conname = 'fk_{oldTableName}_customer'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Old-named FK should be gone");

        conn.Close();
    }

    [Test]
    public void ShouldAddExcludeConstraint()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"excl_test_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Ensure btree_gist extension exists (required for exclude constraints on scalar types)
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS btree_gist";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Create table without exclude constraint
        cmd.CommandText = $@"
CREATE TABLE public.{tableName} (id INT NOT NULL, room_id INT NOT NULL, during TSRANGE NOT NULL);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with exclude constraint: prevent overlapping bookings for the same room
        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "room_id", "DataType": "INT4", "Nullable": false},
                {"Name": "during", "DataType": "TSRANGE", "Nullable": false}
            ],
            "ExcludeConstraints": [
                {
                    "Name": "excl_{{tableName}}_overlap",
                    "AccessMethod": "gist",
                    "ExcludeColumns": [
                        {"Column": "room_id", "Operator": "="},
                        {"Column": "during", "Operator": "&&"}
                    ]
                }
            ]
        }
        """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false)";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify exclude constraint exists
        cmd.CommandText = $@"
SELECT COUNT(*) FROM pg_constraint c
JOIN pg_class t ON c.conrelid = t.oid
JOIN pg_namespace n ON t.relnamespace = n.oid
WHERE n.nspname = 'public' AND t.relname = '{tableName}'
  AND c.conname = 'excl_{tableName}_overlap' AND c.contype = 'x'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Exclude constraint should exist");

        // Verify it actually prevents overlapping ranges for the same room
        cmd.CommandText = $"INSERT INTO public.{tableName} (id, room_id, during) VALUES (1, 100, '[2026-01-01, 2026-01-10)')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"INSERT INTO public.{tableName} (id, room_id, during) VALUES (2, 100, '[2026-01-05, 2026-01-15)')";
        var ex = Assert.Throws<Npgsql.PostgresException>(() => cmd.ExecuteNonQuery());
        Assert.That(ex!.SqlState, Is.EqualTo("23P01"), "Should fail with exclusion violation");

        conn.Close();
    }

    [Test]
    public void ShouldNotCreateTableInWhatIfMode()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"whatif_tbl_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create the table with one column
        cmd.CommandText = $@"CREATE TABLE public.{tableName} (id INT NOT NULL)";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // WhatIf quench that adds a new column — should NOT actually add it
        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false},
                {"Name": "new_col", "DataType": "INT4", "Nullable": true}
            ]
        }
        """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := true, p_DropUnknownIndexes := false, p_DropTablesRemovedFromProduct := false)";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{tableName}' AND column_name = 'new_col')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "Column should not be added in WhatIf mode");

        conn.Close();
    }

    [Test]
    public void ShouldCreateDeferrableForeignKey()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var parentTable = $"def_parent_{uniqueId}";
        var childTable = $"def_child_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create parent and child tables without FK
        cmd.CommandText = $@"
CREATE TABLE public.{parentTable} (id INT NOT NULL PRIMARY KEY);
CREATE TABLE public.{childTable} (id INT NOT NULL, parent_id INT NOT NULL);
INSERT INTO public.{parentTable} (id) VALUES (1);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with deferrable initially deferred FK
        var json = $$"""
        [
            {
                "Schema": "public",
                "Name": "{{parentTable}}",
                "Columns": [
                    {"Name": "id", "DataType": "INT4", "Nullable": false}
                ],
                "Indexes": [
                    {"Name": "{{parentTable}}_pkey", "PrimaryKey": true, "Unique": true, "IndexColumns": "id"}
                ]
            },
            {
                "Schema": "public",
                "Name": "{{childTable}}",
                "Columns": [
                    {"Name": "id", "DataType": "INT4", "Nullable": false},
                    {"Name": "parent_id", "DataType": "INT4", "Nullable": false}
                ],
                "ForeignKeys": [
                    {
                        "Name": "fk_{{childTable}}_parent",
                        "Columns": "parent_id",
                        "RelatedTableSchema": "public",
                        "RelatedTable": "{{parentTable}}",
                        "RelatedColumns": "id",
                        "Deferrable": true,
                        "InitiallyDeferred": true
                    }
                ]
            }
        ]
        """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false)";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify FK is deferrable and initially deferred
        cmd.CommandText = $@"
SELECT c.condeferrable, c.condeferred
FROM pg_constraint c
JOIN pg_class t ON c.conrelid = t.oid
JOIN pg_namespace n ON t.relnamespace = n.oid
WHERE n.nspname = 'public' AND t.relname = '{childTable}'
  AND c.conname = 'fk_{childTable}_parent' AND c.contype = 'f'";
        using var reader = cmd.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "FK constraint should exist");
        Assert.That(reader.GetBoolean(0), Is.True, "FK should be DEFERRABLE");
        Assert.That(reader.GetBoolean(1), Is.True, "FK should be INITIALLY DEFERRED");
        reader.Close();

        // Verify deferred behavior: insert child with non-existent parent_id inside a transaction,
        // then insert the parent before commit — should succeed with deferred FK
        cmd.CommandText = $@"
BEGIN;
INSERT INTO public.{childTable} (id, parent_id) VALUES (1, 999);
INSERT INTO public.{parentTable} (id) VALUES (999);
COMMIT;
";
        Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(), "Deferred FK should allow temporary violation within transaction");

        conn.Close();
    }

    // The referential actions belong before DEFERRABLE: written after it, the ALTER TABLE was a syntax error
    // ("syntax error at or near ON"), so a deferrable key with any action could never be created.
    [Test]
    public void ShouldCreateDeferrableForeignKeyWithReferentialActions()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var parentTable = $"defa_parent_{uniqueId}";
        var childTable = $"defa_child_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = $$"""
        [
            {
                "Schema": "public",
                "Name": "{{parentTable}}",
                "Columns": [ {"Name": "id", "DataType": "INT4", "Nullable": false} ],
                "Indexes": [ {"Name": "{{parentTable}}_pkey", "PrimaryKey": true, "Unique": true, "IndexColumns": "id"} ]
            },
            {
                "Schema": "public",
                "Name": "{{childTable}}",
                "Columns": [
                    {"Name": "id", "DataType": "INT4", "Nullable": false},
                    {"Name": "parent_id", "DataType": "INT4", "Nullable": true}
                ],
                "ForeignKeys": [
                    {
                        "Name": "fk_{{childTable}}_parent",
                        "Columns": "parent_id",
                        "RelatedTableSchema": "public",
                        "RelatedTable": "{{parentTable}}",
                        "RelatedColumns": "id",
                        "MatchType": "SIMPLE",
                        "DeleteAction": "CASCADE",
                        "UpdateAction": "SET NULL",
                        "Deferrable": true,
                        "InitiallyDeferred": true
                    }
                ]
            }
        ]
        """;

        cmd.CommandText = $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $@"
SELECT c.condeferrable, c.condeferred, c.confdeltype::TEXT, c.confupdtype::TEXT
FROM pg_constraint c
JOIN pg_class t ON c.conrelid = t.oid
JOIN pg_namespace n ON t.relnamespace = n.oid
WHERE n.nspname = 'public' AND t.relname = '{childTable}'
  AND c.conname = 'fk_{childTable}_parent' AND c.contype = 'f'";
        using var reader = cmd.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "FK constraint should exist");
        Assert.Multiple(() =>
        {
            Assert.That(reader.GetBoolean(0), Is.True, "FK should be DEFERRABLE");
            Assert.That(reader.GetBoolean(1), Is.True, "FK should be INITIALLY DEFERRED");
            Assert.That(reader.GetString(2), Is.EqualTo("c"), "ON DELETE CASCADE");
            Assert.That(reader.GetString(3), Is.EqualTo("n"), "ON UPDATE SET NULL");
        });
        reader.Close();

        conn.Close();
    }

    [Test]
    public void ShouldNotRecreateDeferrablePrimaryKeyOnSecondIndexOnlyQuench()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"def_pk_{uniqueId}";
        var pkName = $"pk_{tableName}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Pre-create the table with a DEFERRABLE primary key via raw DDL (a realistic pre-existing
        // deferrable PK). The quench then only has to recognize it as unchanged, not create it.
        cmd.CommandText = $@"
CREATE TABLE public.{tableName} (id INT NOT NULL);
ALTER TABLE public.{tableName} ADD CONSTRAINT ""{pkName}"" PRIMARY KEY (id) DEFERRABLE;";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Indexes": [
                {"Name": "{{pkName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id", "Deferrable": true}
            ]
        }
        """;

        cmd.CommandText = $@"
SELECT c.oid::text, c.condeferrable
FROM pg_constraint c
JOIN pg_class t ON c.conrelid = t.oid
JOIN pg_namespace n ON t.relnamespace = n.oid
WHERE n.nspname = 'public' AND t.relname = '{tableName}' AND c.contype = 'p'";
        string baselineOid;
        using (var reader = cmd.ExecuteReader())
        {
            Assert.That(reader.Read(), Is.True, "Deferrable primary key should exist before quench");
            baselineOid = reader.GetString(0);
            Assert.That(reader.GetBoolean(1), Is.True, "Primary key should be DEFERRABLE before quench");
        }

        // The quench must be a no-op — the deferrable PK must NOT phantom drop/recreate.
        RunTableQuenchProc(cmd, json, indexOnly: true);

        cmd.CommandText = $@"
SELECT c.oid::text, c.condeferrable
FROM pg_constraint c
JOIN pg_class t ON c.conrelid = t.oid
JOIN pg_namespace n ON t.relnamespace = n.oid
WHERE n.nspname = 'public' AND t.relname = '{tableName}' AND c.contype = 'p'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.That(reader.Read(), Is.True, "Deferrable primary key should still exist after quench");
            Assert.That(reader.GetString(0), Is.EqualTo(baselineOid), "Primary key must not be dropped/recreated by the quench (stable oid)");
            Assert.That(reader.GetBoolean(1), Is.True, "Primary key should remain DEFERRABLE after quench");
        }

        conn.Close();
    }

    [Test]
    public void ShouldCreateDeferrablePrimaryKeyFromScratch()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"def_pk_new_{uniqueId}";
        var pkName = $"pk_{tableName}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Create the table with no primary key at all — the quench must ADD a brand new
        // deferrable PK, exercising the "Add Missing Indexes" DDL-generation branch.
        cmd.CommandText = $@"CREATE TABLE public.{tableName} (id INT NOT NULL)";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "id", "DataType": "INT4", "Nullable": false}
            ],
            "Indexes": [
                {"Name": "{{pkName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id", "Deferrable": true, "InitiallyDeferred": true, "FillFactor": 90}
            ]
        }
        """;

        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: productName),
            "Creating a deferrable primary key with a fillfactor from scratch should not raise a 42601 syntax error");

        cmd.CommandText = $@"
SELECT c.condeferrable, c.condeferred
FROM pg_constraint c
JOIN pg_class t ON c.conrelid = t.oid
JOIN pg_namespace n ON t.relnamespace = n.oid
WHERE n.nspname = 'public' AND t.relname = '{tableName}' AND c.conname = '{pkName}' AND c.contype = 'p'";
        using var reader = cmd.ExecuteReader();
        Assert.That(reader.Read(), Is.True, "Primary key constraint should exist");
        Assert.That(reader.GetBoolean(0), Is.True, "Primary key should be DEFERRABLE");
        Assert.That(reader.GetBoolean(1), Is.True, "Primary key should be INITIALLY DEFERRED");
        reader.Close();

        conn.Close();
    }

    [Test]
    public void ShouldCreateIndexWithOperatorClass()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"opclass_{uniqueId}";
        var indexName = $"idx_{tableName}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"CREATE TABLE public.{tableName} (name TEXT NOT NULL)";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // text_pattern_ops is a built-in operator class (no extension required) that reproduces the
        // reported "column opclass" spec shape (e.g. pgvector's "embedding vector_l2_ops") without
        // needing pgvector: QuoteIndexColumnList must split the trailing opclass token off before
        // quoting, or it folds "name text_pattern_ops" into one bogus quoted identifier.
        var json = $$"""
        {
            "Schema": "public",
            "Name": "{{tableName}}",
            "Columns": [
                {"Name": "name", "DataType": "TEXT", "Nullable": false}
            ],
            "Indexes": [
                {"Name": "{{indexName}}", "IndexColumns": "name text_pattern_ops"}
            ]
        }
        """;

        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json),
            "An index column spec with a trailing operator class must not be folded into a single bogus quoted identifier");

        cmd.CommandText = $@"SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{tableName}' AND indexname = '{indexName}')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "Index with an explicit operator class should be created");

        cmd.CommandText = $@"SELECT opc.opcname FROM pg_index idx
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            JOIN pg_opclass opc ON opc.oid = idx.indclass[0]
                            WHERE i.relname = '{indexName}'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("text_pattern_ops"), "The operator class must be applied to the column, not embedded in a bogus identifier");

        conn.Close();
    }
}
