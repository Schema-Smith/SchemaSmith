// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_Tier3Tests : BaseTableQuenchTests
{
    [Test]
    public void ShouldCreateGinIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'Tier3Tests' AND tablename = 'GinIndexTable' AND indexname = 'IDX_GinIndex')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @"SELECT am.amname FROM pg_index idx
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            JOIN pg_am am ON am.oid = i.relam
                            WHERE i.relname = 'IDX_GinIndex'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("gin"));

        conn.Close();
    }

    [Test]
    public void ShouldCreateGinIndexIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'Tier3Tests' AND tablename = 'GinIndexTableIO' AND indexname = 'IDX_GinIndexIO')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @"SELECT am.amname FROM pg_index idx
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            JOIN pg_am am ON am.oid = i.relam
                            WHERE i.relname = 'IDX_GinIndexIO'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("gin"));

        conn.Close();
    }

    [Test]
    public void ShouldCreateBrinIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'Tier3Tests' AND tablename = 'BrinIndexTable' AND indexname = 'IDX_BrinIndex')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @"SELECT am.amname FROM pg_index idx
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            JOIN pg_am am ON am.oid = i.relam
                            WHERE i.relname = 'IDX_BrinIndex'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("brin"));

        conn.Close();
    }

    [Test]
    public void ShouldCreateBrinIndexIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'Tier3Tests' AND tablename = 'BrinIndexTableIO' AND indexname = 'IDX_BrinIndexIO')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @"SELECT am.amname FROM pg_index idx
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            JOIN pg_am am ON am.oid = i.relam
                            WHERE i.relname = 'IDX_BrinIndexIO'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("brin"));

        conn.Close();
    }

    [Test]
    public void ShouldCreateHashIndexWithFillFactor()
    {
        // hash is a non-btree built-in access method (no extension needed) that accepts fillfactor,
        // exercising the same allow-list gate that excludes gin/brin above without requiring pgvector.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'Tier3Tests' AND tablename = 'HashIndexTable' AND indexname = 'IDX_HashIndex')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = @"SELECT am.amname FROM pg_index idx
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            JOIN pg_am am ON am.oid = i.relam
                            WHERE i.relname = 'IDX_HashIndex'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("hash"));

        cmd.CommandText = @"SELECT SPLIT_PART(opt, '=', 2) FROM pg_class c, UNNEST(c.reloptions) AS opt
                            WHERE c.relname = 'IDX_HashIndex' AND opt LIKE 'fillfactor=%'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("70"), "hash accepts fillfactor and must not be skipped by the allow-list");

        conn.Close();
    }

    // Index storage parameters -- the WITH clause. gin's fastupdate and brin's pages_per_range are
    // built-in stand-ins for pgvector's hnsw m / ef_construction and ivfflat lists: identical reloptions
    // plumbing, no extension needed. Self-contained (own schema) so it doesn't lean on the shared setup.
    [Test]
    public void ShouldDeployConvergeAndRoundTripIndexStorageParameters()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = @"DROP SCHEMA IF EXISTS ""StorageParams"" CASCADE; CREATE SCHEMA ""StorageParams"";
                            CREATE TABLE ""StorageParams"".""T"" (""Tags"" JSONB NOT NULL, ""N"" INT NOT NULL);";
        cmd.ExecuteNonQuery();

        string Reloptions(string idx) =>
            $@"SELECT COALESCE(ARRAY_TO_STRING(ARRAY(SELECT opt FROM pg_class c, UNNEST(c.reloptions) AS opt
                        WHERE c.relname = '{idx}' AND opt NOT LIKE 'fillfactor=%' ORDER BY opt), ','), '')";

        string Package(string ginParams, string brinParams) => $$"""
            [{
                "Schema": "StorageParams", "Name": "T",
                "Columns": [ { "Name": "Tags", "DataType": "JSONB", "Nullable": false },
                             { "Name": "N", "DataType": "INT", "Nullable": false } ],
                "Indexes": [
                    { "Name": "IX_gin", "IndexColumns": "Tags", "AccessMethod": "gin", "StorageParameters": { {{ginParams}} } },
                    { "Name": "IX_brin", "IndexColumns": "N", "AccessMethod": "brin", "StorageParameters": { {{brinParams}} } }
                ]
            }]
            """;

        // 1. CREATE carries the declared params.
        RunTableQuenchProc(cmd, Package("\"fastupdate\": \"off\"", "\"pages_per_range\": \"32\""));
        cmd.CommandText = Reloptions("IX_gin");
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("fastupdate=off"),
            "the gin index must carry its declared storage parameter -- this is the mechanism a vector "
            + "index's m / ef_construction rides");
        cmd.CommandText = Reloptions("IX_brin");
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("pages_per_range=32"));

        // 2. Redeploying the same thing is a no-op -- the compare canonicalises, so reloptions' own
        //    ordering does not cause phantom churn.
        var beforeOid = ScalarLong(cmd, "SELECT '\"StorageParams\".\"IX_gin\"'::regclass::oid");
        RunTableQuenchProc(cmd, Package("\"fastupdate\": \"off\"", "\"pages_per_range\": \"32\""));
        Assert.That(ScalarLong(cmd, "SELECT '\"StorageParams\".\"IX_gin\"'::regclass::oid"), Is.EqualTo(beforeOid),
            "an unchanged storage parameter must not drop and recreate the index");

        // 3. Changing a parameter rebuilds the index (a new oid proves drop+recreate, which is required --
        //    several of these cannot be ALTERed in place).
        RunTableQuenchProc(cmd, Package("\"fastupdate\": \"on\"", "\"pages_per_range\": \"64\""));
        cmd.CommandText = Reloptions("IX_gin");
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("fastupdate=on"), "the changed parameter must be applied");
        Assert.That(ScalarLong(cmd, "SELECT '\"StorageParams\".\"IX_gin\"'::regclass::oid"), Is.Not.EqualTo(beforeOid),
            "a storage-parameter change must rebuild the index, not silently no-op");

        // 4. Round-trips through extraction.
        cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"('StorageParams', 'T')";
        var json = cmd.ExecuteScalar()?.ToString() ?? "";
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("StorageParameters"), json);
            Assert.That(json, Does.Contain("fastupdate"), json);
            Assert.That(json, Does.Contain("pages_per_range"), json);
        });

        cmd.CommandText = @"DROP SCHEMA IF EXISTS ""StorageParams"" CASCADE;";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private static long ScalarLong(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return System.Convert.ToInt64(cmd.ExecuteScalar());
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE SCHEMA ""Tier3Tests"";
--ShouldCreateGinIndex
CREATE TABLE ""Tier3Tests"".""GinIndexTable"" (""Tags"" JSONB NOT NULL);
--ShouldCreateBrinIndex
CREATE TABLE ""Tier3Tests"".""BrinIndexTable"" (""CreatedAt"" TIMESTAMP NOT NULL);
--ShouldCreateHashIndexWithFillFactor
CREATE TABLE ""Tier3Tests"".""HashIndexTable"" (""Code"" TEXT NOT NULL);

-- Index Only
--ShouldCreateGinIndexIndexOnly
CREATE TABLE ""Tier3Tests"".""GinIndexTableIO"" (""Tags"" JSONB NOT NULL);
--ShouldCreateBrinIndexIndexOnly
CREATE TABLE ""Tier3Tests"".""BrinIndexTableIO"" (""CreatedAt"" TIMESTAMP NOT NULL);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "Tier3Tests",
                "Name": "GinIndexTable",
                "Columns": [
                    {
                      "Name": "Tags",
                      "DataType": "JSONB",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_GinIndex",
                      "IndexColumns": "Tags",
                      "AccessMethod": "gin"
                    }
                ]
            },
            {
                "Schema": "Tier3Tests",
                "Name": "BrinIndexTable",
                "Columns": [
                    {
                      "Name": "CreatedAt",
                      "DataType": "TIMESTAMP",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_BrinIndex",
                      "IndexColumns": "CreatedAt",
                      "AccessMethod": "brin"
                    }
                ]
            },
            {
                "Schema": "Tier3Tests",
                "Name": "HashIndexTable",
                "Columns": [
                    {
                      "Name": "Code",
                      "DataType": "TEXT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                      "Name": "IDX_HashIndex",
                      "IndexColumns": "Code",
                      "AccessMethod": "hash",
                      "FillFactor": 70
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
                "Schema": "Tier3Tests",
                "Name": "GinIndexTableIO",
                "Indexes": [
                    {
                      "Name": "IDX_GinIndexIO",
                      "IndexColumns": "Tags",
                      "AccessMethod": "gin"
                    }
                ]
            },
            {
                "Schema": "Tier3Tests",
                "Name": "BrinIndexTableIO",
                "Indexes": [
                    {
                      "Name": "IDX_BrinIndexIO",
                      "IndexColumns": "CreatedAt",
                      "AccessMethod": "brin"
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json, indexOnly: true);
        conn.Close();
    }
}
