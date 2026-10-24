// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropColumnsTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldHandleRemovingColumnUsedInIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnInIndex' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnInIndex' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'DropColumnsTests' AND tablename = 'DropColumnInIndex' AND indexname = 'IDX_Dependency')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'DropColumnsTests' AND tablename = 'DropColumnInIndex' AND indexname = 'IDX_NoDependency')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnUsedInUniqueIndexReferencedByForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnInUniqueIndexInFK' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnInUniqueIndexInFK' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'DropColumnsTests' AND tablename = 'DropColumnInUniqueIndexInFK' AND indexname = 'IDX_Dependency3')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_DropColumnInUniqueIndexInFK_SelfRef' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithDefault' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithDefault' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithTableCheckConstraint' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithTableCheckConstraint' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithStatistics()
    {
        if (PgServerMajor() < 14) Assert.Ignore("Expression statistics (CREATE STATISTICS on an expression) require PostgreSQL 14+.");
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithStatistics' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithStatistics' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithIndexFilterExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithIndexFilter' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithIndexFilter' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithFK' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithFK' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleRemovingColumnWithComputedExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithComputed' AND column_name = 'Column2')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False);

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnWithComputed' AND column_name = 'Column1')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldSuppressDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // The suppressed table: OrphanedColumn was removed from JSON + flag = false → column survives
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnSuppressed' AND column_name = 'OrphanedColumn')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "OrphanedColumn should still exist (suppressed by table flag)");

        // The control table: flag absent (inherits default = true) → column was dropped
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema = 'DropColumnsTests' AND table_name = 'DropColumnControl' AND column_name = 'OrphanedColumn')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "OrphanedColumn should be gone (no suppression flag)");

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
CREATE SCHEMA ""DropColumnsTests"";
--TableQuench_ShouldHandleRemovingColumnUsedInIndex
CREATE TABLE ""DropColumnsTests"".""DropColumnInIndex"" (""Column1"" INT NOT NULL, ""Column2"" INT);
CREATE INDEX ""IDX_NoDependency"" ON ""DropColumnsTests"".""DropColumnInIndex"" (""Column1"");
CREATE INDEX ""IDX_Dependency"" ON ""DropColumnsTests"".""DropColumnInIndex"" (""Column2"");
--TableQuench_ShouldHandleRemovingColumnUsedInUniqueIndexReferencedByForeignKey
CREATE TABLE ""DropColumnsTests"".""DropColumnInUniqueIndexInFK"" (""Column1"" INT NOT NULL, ""Column2"" INT NOT NULL);
CREATE UNIQUE INDEX ""IDX_Dependency2"" ON ""DropColumnsTests"".""DropColumnInUniqueIndexInFK"" (""Column2"");
ALTER TABLE ""DropColumnsTests"".""DropColumnInUniqueIndexInFK"" ADD CONSTRAINT ""FK_DropColumnInUniqueIndexInFK_SelfRef"" FOREIGN KEY (""Column1"") REFERENCES ""DropColumnsTests"".""DropColumnInUniqueIndexInFK"" (""Column2"");
--TableQuench_ShouldHandleRemovingColumnWithDefault
CREATE TABLE ""DropColumnsTests"".""DropColumnWithDefault"" (""Column1"" INT NOT NULL, ""Column2"" INT DEFAULT 0);
--TableQuench_ShouldHandleRemovingColumnWithTableLevelCheckConstraint
CREATE TABLE ""DropColumnsTests"".""DropColumnWithTableCheckConstraint"" (""Column1"" INT NOT NULL, ""Column2"" INT, CONSTRAINT ""CK_DropColumnWithTableCheckConstraint_Dependency"" CHECK (""Column2"" < ""Column1""));
--TableQuench_ShouldHandleRemovingColumnWithStatistics
CREATE TABLE ""DropColumnsTests"".""DropColumnWithStatistics"" (""Column1"" INT NOT NULL, ""Column2"" INT);
DO $$ BEGIN IF (current_setting('server_version_num')::int / 10000) >= 14 THEN EXECUTE 'CREATE STATISTICS ""DropColumnsTests"".""ST_Dependency"" ON (""Column2"" / 1) FROM ""DropColumnsTests"".""DropColumnWithStatistics""'; END IF; END $$;
--TableQuench_ShouldHandleRemovingColumnWithIndexFilterExpression
CREATE TABLE ""DropColumnsTests"".""DropColumnWithIndexFilter"" (""Column1"" INT NOT NULL, ""Column2"" INT);
CREATE INDEX ""IDX_Dependency3"" ON ""DropColumnsTests"".""DropColumnWithIndexFilter"" (""Column1"") WHERE ""Column2"" < 100;
--TableQuench_ShouldHandleRemovingColumnWithForeignKey
CREATE TABLE ""DropColumnsTests"".""DropColumnWithFK"" (""Column1"" INT NOT NULL, ""Column2"" INT, CONSTRAINT ""PK_DropColumnWithFK"" PRIMARY KEY (""Column1""));
CREATE UNIQUE INDEX ""UQ_Colum2"" ON ""DropColumnsTests"".""DropColumnWithFK"" (""Column2"");
CREATE TABLE ""DropColumnsTests"".""DropColumnWithFKRef"" (""Column1"" INT NOT NULL, ""Column2"" INT, CONSTRAINT ""PK_DropColumnWithFKRef"" PRIMARY KEY (""Column1""));
ALTER TABLE ""DropColumnsTests"".""DropColumnWithFK"" ADD CONSTRAINT ""FK_DropColumnWithFK_SelfRef"" FOREIGN KEY (""Column2"") REFERENCES ""DropColumnsTests"".""DropColumnWithFK"" (""Column1"");
ALTER TABLE ""DropColumnsTests"".""DropColumnWithFKRef"" ADD CONSTRAINT ""FK_DropColumnWithFK_Referenced"" FOREIGN KEY (""Column2"") REFERENCES ""DropColumnsTests"".""DropColumnWithFK"" (""Column2"");
ALTER TABLE ""DropColumnsTests"".""DropColumnWithFK"" ADD CONSTRAINT ""FK_DropColumnWithFK_Referencing"" FOREIGN KEY (""Column2"") REFERENCES ""DropColumnsTests"".""DropColumnWithFKRef"" (""Column1"");
--TableQuench_ShouldHandleRemovingColumnWithComputedExpression
CREATE TABLE ""DropColumnsTests"".""DropColumnWithComputed"" (""Column1"" INT NOT NULL, ""Column2"" INT, ""Column3"" INT GENERATED ALWAYS AS (""Column2"" * 3) STORED);
--TableQuench_ShouldSuppressDropWhenTableFlagIsFalse
CREATE TABLE ""DropColumnsTests"".""DropColumnSuppressed"" (""Column1"" INT NOT NULL, ""OrphanedColumn"" INT);
CREATE TABLE ""DropColumnsTests"".""DropColumnControl"" (""Column1"" INT NOT NULL, ""OrphanedColumn"" INT);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnInIndex",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnInUniqueIndexInFK",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithDefault",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithCheckConstraint",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithTableCheckConstraint",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithStatistics",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithIndexFilter",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithFK",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnWithComputed",
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "Column3",
                      "DataType": "INT",
                      "Generated": "ALWAYS",
                      "GenerationExpression": "\"Column1\" * 3"
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        // Second quench: exercises table-level DropColumnsRemovedFromProduct flag.
        // DropColumnSuppressed carries "DropColumnsRemovedFromProduct": false → OrphanedColumn survives.
        // DropColumnControl has no flag (null → inherits cascade default=true) → OrphanedColumn drops.
        var flagJson = """
            [
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnSuppressed",
                "DropColumnsRemovedFromProduct": false,
                "Columns": [
                    {
                      "Name": "Column1",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "DropColumnsTests",
                "Name": "DropColumnControl",
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
        RunTableQuenchProc(cmd, flagJson);

        conn.Close();
    }
}
