// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Verifies TableQuench's ability to EXTEND an existing primary key with additional columns
/// (e.g. PK(a, b, c) -> PK(a, b, c, d, e)). The slice-2 CompletedMigrationScripts migration
/// relies on this capability — and at the time these tests were added, no existing test exercised
/// it. Lives at the TableQuench level rather than the kindling level so all consumers of
/// TableQuench (not just our specific kindling SQL) are covered.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PrimaryKeyExtensionTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldExtendPrimaryKeyFromOneColumnToTwo()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT STRING_AGG(c.[name], ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
              FROM sys.indexes i WITH (NOLOCK)
              JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
              JOIN sys.columns c WITH (NOLOCK) ON c.[object_id] = i.[object_id] AND c.column_id = ic.column_id
              WHERE i.[object_id] = OBJECT_ID('dbo.ExtendPkOneToTwo') AND i.is_primary_key = 1";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column1,Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldExtendPrimaryKeyFromThreeColumnsToFive_MirrorsCompletedMigrationScriptsScenario()
    {
        // This is the exact shape of the SchemaSmith.CompletedMigrationScripts migration:
        // a 3-column PK (ScriptPath, ProductName, QuenchSlot) extended to 5 columns by
        // adding template_name and schema_name.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT STRING_AGG(c.[name], ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
              FROM sys.indexes i WITH (NOLOCK)
              JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
              JOIN sys.columns c WITH (NOLOCK) ON c.[object_id] = i.[object_id] AND c.column_id = ic.column_id
              WHERE i.[object_id] = OBJECT_ID('dbo.ExtendPkThreeToFive') AND i.is_primary_key = 1";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PathCol,ProductCol,SlotCol,TemplateCol,SchemaCol"));
    }

    [Test]
    public void ExtendedPrimaryKey_AcceptsRowsThatWouldHaveCollidedOnLegacyPk()
    {
        // Behavioral assertion: after PK extension, two rows differing only by the new key
        // columns must coexist. (If TableQuench failed to extend the PK in OneTimeSetUp,
        // these inserts would have collided on the legacy 3-col PK and the setup would have
        // failed before any test ran — but explicitly assert the post-quench INSERT behavior
        // here so failures point at the right test.)
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM dbo.ExtendPkThreeToFive";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(2),
            "OneTimeSetUp must have inserted two rows with identical first-three-cols but different new-cols.");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Seed: legacy 1-col PK table and legacy 3-col PK table.
        cmd.CommandText = @"
CREATE TABLE dbo.ExtendPkOneToTwo (
    Column1 INT NOT NULL,
    CONSTRAINT PK_ExtendPkOneToTwo PRIMARY KEY CLUSTERED (Column1)
);

CREATE TABLE dbo.ExtendPkThreeToFive (
    PathCol VARCHAR(800) NOT NULL,
    ProductCol VARCHAR(100) NOT NULL,
    SlotCol VARCHAR(30) NOT NULL,
    CONSTRAINT PK_ExtendPkThreeToFive PRIMARY KEY CLUSTERED (PathCol, ProductCol, SlotCol)
);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Run TableQuench with extended PK definitions.
        var json = """
        [
            {
                "Schema": "[dbo]",
                "Name": "[ExtendPkOneToTwo]",
                "Columns": [
                    { "Name": "[Column1]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Column2]", "DataType": "INT", "Nullable": false, "Default": "0" }
                ],
                "Indexes": [
                    {
                        "Name": "[PK_ExtendPkOneToTwo]",
                        "IndexColumns": "[Column1],[Column2]",
                        "PrimaryKey": true,
                        "Unique": true,
                        "Clustered": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ExtendPkThreeToFive]",
                "Columns": [
                    { "Name": "[PathCol]", "DataType": "VARCHAR(800)", "Nullable": false },
                    { "Name": "[ProductCol]", "DataType": "VARCHAR(100)", "Nullable": false },
                    { "Name": "[SlotCol]", "DataType": "VARCHAR(30)", "Nullable": false },
                    { "Name": "[TemplateCol]", "DataType": "NVARCHAR(256)", "Nullable": false, "Default": "''''" },
                    { "Name": "[SchemaCol]", "DataType": "NVARCHAR(256)", "Nullable": false, "Default": "''''" }
                ],
                "Indexes": [
                    {
                        "Name": "[PK_ExtendPkThreeToFive]",
                        "IndexColumns": "[PathCol],[ProductCol],[SlotCol],[TemplateCol],[SchemaCol]",
                        "PrimaryKey": true,
                        "Unique": true,
                        "Clustered": true
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        // Behavioral check: insert two rows that would have collided on the legacy 3-col PK
        // but are distinct under the extended 5-col PK. If TableQuench failed to extend
        // the PK, the second insert raises a duplicate-key violation and OneTimeSetUp fails.
        cmd.CommandText = @"
INSERT INTO dbo.ExtendPkThreeToFive (PathCol, ProductCol, SlotCol, TemplateCol, SchemaCol)
VALUES ('Migration_001.sql', 'Demo', 'Before', 'TenantA', '');

INSERT INTO dbo.ExtendPkThreeToFive (PathCol, ProductCol, SlotCol, TemplateCol, SchemaCol)
VALUES ('Migration_001.sql', 'Demo', 'Before', 'TenantB', '');";
        cmd.ExecuteNonQuery();
    }
}
