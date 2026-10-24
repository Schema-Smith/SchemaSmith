// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropIndexesTests : BaseTableQuenchTests
{
    // Two-phase: an index must be product-OWNED to take the removed-from-product path (a DDL-created
    // index would read as "unknown", a different axis). Phase 1 quenches the index into existence
    // (stamping ProductName ownership); phase 2 removes it from the JSON. IdxSuppressed sets
    // DropIndexesRemovedFromProduct:false -> survives; IdxControl omits it -> dropped by absence.
    // @DropUnknownIndexes is 0 throughout, so only the removed-from-product path can fire.
    [Test]
    public void TableQuench_ShouldSuppressIndexDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.indexes WHERE [name] = 'IX_IdxSuppressed_Val' AND [object_id] = OBJECT_ID('dbo.IdxSuppressed')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "IX_IdxSuppressed_Val should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.indexes WHERE [name] = 'IX_IdxControl_Val' AND [object_id] = OBJECT_ID('dbo.IdxControl')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "IX_IdxControl_Val should be gone (dropped by absence).");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Phase 1 — quench the tables WITH their nonclustered index (creates + stamps ownership).
        var withIndex = """
            [
            {
                "Schema": "[dbo]", "Name": "[IdxSuppressed]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
                "Indexes": [
                    { "Name": "[PK_IdxSuppressed]", "PrimaryKey": true, "IndexColumns": "[Id]" },
                    { "Name": "[IX_IdxSuppressed_Val]", "IndexColumns": "[Val]" }
                ]
            },
            {
                "Schema": "[dbo]", "Name": "[IdxControl]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
                "Indexes": [
                    { "Name": "[PK_IdxControl]", "PrimaryKey": true, "IndexColumns": "[Id]" },
                    { "Name": "[IX_IdxControl_Val]", "IndexColumns": "[Val]" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withIndex);

        // Phase 2 — remove the nonclustered index from both. IdxSuppressed protects its own.
        var withoutIndex = """
            [
            {
                "Schema": "[dbo]", "Name": "[IdxSuppressed]",
                "DropIndexesRemovedFromProduct": false,
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "[PK_IdxSuppressed]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
            },
            {
                "Schema": "[dbo]", "Name": "[IdxControl]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "[PK_IdxControl]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withoutIndex);

        conn.Close();
    }
}
