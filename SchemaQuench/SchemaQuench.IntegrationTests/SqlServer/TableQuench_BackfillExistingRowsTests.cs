// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// SQL Server leaves existing rows NULL when a NULLABLE column with a default is added, unless the ADD
/// carries WITH VALUES. PostgreSQL, MySQL and MariaDB all backfill instead, so this is the one engine
/// where "new nullable column, existing rows get the default" has to be asked for.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_BackfillExistingRowsTests : BaseTableQuenchTests
{
    private const string TableName = "BackfillRows";

    // Both new columns are nullable with the same default; only one asks for the backfill. Declaring
    // them together is the point -- WITH VALUES is per COLUMN, not per statement, and these two land in
    // a single ALTER TABLE ... ADD. A per-statement implementation would backfill both and still pass a
    // test that only looked at the opted-in column.
    private string TableJson(bool includeNewColumns) => $$"""
        [{
            "Schema": "[dbo]",
            "Name": "[{{TableName}}]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false }
                {{(includeNewColumns
                    ? """
                      ,{ "Name": "[OptedIn]", "DataType": "INT", "Nullable": true, "Default": "7", "BackfillExistingRows": true },
                       { "Name": "[Untouched]", "DataType": "INT", "Nullable": true, "Default": "7" }
                      """
                    : "")}}
            ],
            "Indexes": [ { "Name": "[PK_{{TableName}}]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true, "Clustered": true } ]
        }]
        """;

    [Test]
    public void AddingANullableColumn_BackfillsExistingRows_OnlyWhenAsked()
    {
        using var conn = (DbConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{TableName}";
        cmd.ExecuteNonQuery();

        // The rows must pre-date the column, which is the whole situation WITH VALUES exists for.
        RunTableQuenchProc(cmd, TableJson(includeNewColumns: false));
        cmd.CommandText = $"INSERT INTO dbo.{TableName} (Id) VALUES (1), (2)";
        cmd.ExecuteNonQuery();

        RunTableQuenchProc(cmd, TableJson(includeNewColumns: true));

        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.{TableName} WHERE OptedIn = 7";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2),
            "BackfillExistingRows must apply the default to rows that already existed");

        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.{TableName} WHERE Untouched IS NULL";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2),
            "a column that did not ask for the backfill must still leave existing rows NULL");

        // The default itself must still be in place for rows added afterwards -- WITH VALUES populates
        // history, it does not replace the DEFAULT constraint.
        cmd.CommandText = $"INSERT INTO dbo.{TableName} (Id) VALUES (3)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.{TableName} WHERE Id = 3 AND OptedIn = 7 AND Untouched = 7";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1),
            "the DEFAULT must still apply to new rows on both columns");

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{TableName}";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
