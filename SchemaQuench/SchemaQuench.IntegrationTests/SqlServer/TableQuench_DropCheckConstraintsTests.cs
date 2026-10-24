// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropCheckConstraintsTests : BaseTableQuenchTests
{
    // Non-vacuous + the ### Fixed: SQL Server previously did NOT drop a table-level check removed
    // from the product (only PostgreSQL did). Now it does by default. ChkControl's check is removed
    // from the JSON with no flag -> dropped (the normalization); ChkSuppressed sets
    // DropCheckConstraintsRemovedFromProduct:false -> its check survives in the same quench.
    [Test]
    public void TableQuench_ShouldSuppressCheckDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.CK_ChkSuppressed_Pos') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "CK_ChkSuppressed_Pos should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.CK_ChkControl_Pos') IS NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "CK_ChkControl_Pos should be gone (dropped by absence, the normalization).");

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

        // Multi-column checks (reference both Id and Val) so SQL Server stores them as table-level
        // constraints (parent_column_id = 0); a single-column check is treated as a column check.
        cmd.CommandText = @"
CREATE TABLE dbo.ChkSuppressed (Id INT NOT NULL, Val INT, CONSTRAINT CK_ChkSuppressed_Pos CHECK (Val > Id))
CREATE TABLE dbo.ChkControl (Id INT NOT NULL, Val INT, CONSTRAINT CK_ChkControl_Pos CHECK (Val > Id))
";
        cmd.ExecuteNonQuery();

        // Both tables drop their (table-level) check from the JSON. ChkSuppressed protects its own.
        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[ChkSuppressed]",
                "DropCheckConstraintsRemovedFromProduct": false,
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Val]", "DataType": "INT", "Nullable": true }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ChkControl]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Val]", "DataType": "INT", "Nullable": true }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
