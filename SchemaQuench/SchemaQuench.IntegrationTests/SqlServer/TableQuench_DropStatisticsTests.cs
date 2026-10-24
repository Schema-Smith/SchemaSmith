// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropStatisticsTests : BaseTableQuenchTests
{
    // Non-vacuous + the ### Fixed: SQL Server previously dropped a user-created statistics object
    // only as a side effect of a column change, never by absence (only PostgreSQL did). Now it does
    // by default. StatControl's statistic is removed from the JSON with no flag -> dropped;
    // StatSuppressed sets DropStatisticsRemovedFromProduct:false -> its statistic survives.
    [Test]
    public void TableQuench_ShouldSuppressStatisticsDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.stats WHERE [name] = 'ST_StatSuppressed' AND [object_id] = OBJECT_ID('dbo.StatSuppressed')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "ST_StatSuppressed should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.stats WHERE [name] = 'ST_StatControl' AND [object_id] = OBJECT_ID('dbo.StatControl')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "ST_StatControl should be gone (dropped by absence, the normalization).");

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

        cmd.CommandText = @"
CREATE TABLE dbo.StatSuppressed (Id INT, Col2 INT)
CREATE STATISTICS ST_StatSuppressed ON dbo.StatSuppressed (Id, Col2)
CREATE TABLE dbo.StatControl (Id INT, Col2 INT)
CREATE STATISTICS ST_StatControl ON dbo.StatControl (Id, Col2)
";
        cmd.ExecuteNonQuery();

        // Both tables drop their statistics from the JSON. StatSuppressed protects its own.
        var json = """
            [
            {
                "Schema": "[dbo]", "Name": "[StatSuppressed]",
                "DropStatisticsRemovedFromProduct": false,
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": true }, { "Name": "[Col2]", "DataType": "INT", "Nullable": true } ]
            },
            {
                "Schema": "[dbo]", "Name": "[StatControl]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": true }, { "Name": "[Col2]", "DataType": "INT", "Nullable": true } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
