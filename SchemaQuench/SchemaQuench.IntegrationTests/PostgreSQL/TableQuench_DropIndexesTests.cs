// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropIndexesTests : BaseTableQuenchTests
{
    // Two-phase: an index must be product-OWNED to take the removed-from-product path. Phase 1
    // quenches the index into existence (recording ProductOwnership); phase 2 removes it from the
    // JSON. IdxSuppressed sets DropIndexesRemovedFromProduct:false -> survives; IdxControl omits it
    // -> dropped by absence. p_DropUnknownIndexes is false throughout, isolating the removed path.
    [Test]
    public void TableQuench_ShouldSuppressIndexDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_indexes WHERE schemaname = 'DropIdxTests' AND indexname = 'IX_IdxSuppressed_Val')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "IX_IdxSuppressed_Val should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_indexes WHERE schemaname = 'DropIdxTests' AND indexname = 'IX_IdxControl_Val')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "IX_IdxControl_Val should be gone (dropped by absence).");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = @"CREATE SCHEMA ""DropIdxTests"";";
        cmd.ExecuteNonQuery();

        // Phase 1 — quench the tables WITH their secondary index (creates + records ownership).
        var withIndex = """
            [
            {
                "Schema": "DropIdxTests", "Name": "IdxSuppressed",
                "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false }, { "Name": "Val", "DataType": "INT4", "Nullable": true } ],
                "Indexes": [
                    { "Name": "PK_IdxSuppressed", "PrimaryKey": true, "IndexColumns": "Id" },
                    { "Name": "IX_IdxSuppressed_Val", "IndexColumns": "Val" }
                ]
            },
            {
                "Schema": "DropIdxTests", "Name": "IdxControl",
                "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false }, { "Name": "Val", "DataType": "INT4", "Nullable": true } ],
                "Indexes": [
                    { "Name": "PK_IdxControl", "PrimaryKey": true, "IndexColumns": "Id" },
                    { "Name": "IX_IdxControl_Val", "IndexColumns": "Val" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withIndex);

        // Phase 2 — remove the secondary index from both. IdxSuppressed protects its own.
        var withoutIndex = """
            [
            {
                "Schema": "DropIdxTests", "Name": "IdxSuppressed",
                "DropIndexesRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false }, { "Name": "Val", "DataType": "INT4", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_IdxSuppressed", "PrimaryKey": true, "IndexColumns": "Id" } ]
            },
            {
                "Schema": "DropIdxTests", "Name": "IdxControl",
                "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false }, { "Name": "Val", "DataType": "INT4", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_IdxControl", "PrimaryKey": true, "IndexColumns": "Id" } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withoutIndex);

        conn.Close();
    }
}
