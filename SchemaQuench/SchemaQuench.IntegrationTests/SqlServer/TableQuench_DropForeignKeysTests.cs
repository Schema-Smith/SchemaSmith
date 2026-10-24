// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropForeignKeysTests : BaseTableQuenchTests
{
    // Non-vacuous: both tables have their FK removed from the JSON in the SAME quench.
    // FKDropSuppressed carries "DropForeignKeysRemovedFromProduct": false -> its FK survives.
    // FKDropControl has no flag (null -> inherits cascade default = true) -> its FK is dropped.
    [Test]
    public void TableQuench_ShouldSuppressForeignKeyDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Suppressed table: FK removed from JSON + table flag false -> FK still exists.
        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.FK_FKDropSuppressed_Ref') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "FK_FKDropSuppressed_Ref should still exist (suppressed by table flag).");

        // Control table: FK removed from JSON, no flag -> FK dropped.
        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.FK_FKDropControl_Ref') IS NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "FK_FKDropControl_Ref should be gone (no suppression flag).");

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

        // Create the referenced table and two child tables, each with a foreign key to it.
        cmd.CommandText = @"
CREATE TABLE dbo.FKDropRef (Id INT NOT NULL CONSTRAINT PK_FKDropRef PRIMARY KEY)
CREATE TABLE dbo.FKDropSuppressed (Id INT NOT NULL CONSTRAINT PK_FKDropSuppressed PRIMARY KEY, RefId INT NULL)
CREATE TABLE dbo.FKDropControl (Id INT NOT NULL CONSTRAINT PK_FKDropControl PRIMARY KEY, RefId INT NULL)
ALTER TABLE dbo.FKDropSuppressed ADD CONSTRAINT FK_FKDropSuppressed_Ref FOREIGN KEY (RefId) REFERENCES dbo.FKDropRef (Id)
ALTER TABLE dbo.FKDropControl ADD CONSTRAINT FK_FKDropControl_Ref FOREIGN KEY (RefId) REFERENCES dbo.FKDropRef (Id)
";
        cmd.ExecuteNonQuery();

        // Quench with the foreign keys removed from the table JSON (empty ForeignKeys).
        // FKDropSuppressed sets DropForeignKeysRemovedFromProduct:false; FKDropControl omits it.
        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[FKDropRef]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "[PK_FKDropRef]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[FKDropSuppressed]",
                "DropForeignKeysRemovedFromProduct": false,
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[RefId]", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "[PK_FKDropSuppressed]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[FKDropControl]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[RefId]", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "[PK_FKDropControl]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
