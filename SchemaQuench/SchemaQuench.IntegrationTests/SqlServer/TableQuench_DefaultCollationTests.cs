// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// A column declared with the collation that is the target database's default is physically the same as one at the
// default, which the catalog reports as such. Compared as written, the declaration never matched: every deploy dropped the
// column's indexes, altered it to the collation it had and built them again. Extracting from one database and deploying to
// another whose default is that column's collation does exactly this.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DefaultCollationTests : BaseTableQuenchTests
{
    private static string Json(string table, string collation) => $$"""
[ { "Schema": "[dbo]", "Name": "[{{table}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                 { "Name": "[Code]", "DataType": "VARCHAR(20)", "Nullable": true, "Collation": "{{collation}}" } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                 { "Name": "[IX_{{table}}_Code]", "IndexColumns": "[Code]" } ] } ]
""";

    [Test]
    public void AColumnDeclaredWithTheDatabaseDefault_RedeploysWithoutTouchingIt_AndAnotherCollationStillApplies()
    {
        var table = $"DefColl_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        object Scalar(string sql) { cmd.CommandText = sql; return cmd.ExecuteScalar(); }
        try
        {
            var databaseDefault = Scalar("SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(128))")!.ToString();
            RunTableQuenchProc(cmd, Json(table, databaseDefault!));
            Assert.That(Scalar($"SELECT OBJECT_ID('dbo.{table}')"), Is.Not.Null, "setup: the table exists");

            Scalar("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID; SELECT 1");
            RunTableQuenchProc(cmd, Json(table, databaseDefault!));
            Assert.That(Scalar($"SELECT COALESCE(STRING_AGG(ObjectType + ' ' + ObjectName + ' ' + ActionType, ', '), '') FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%'"),
                Is.Empty, "the redeploy left the column and its index alone");

            var other = databaseDefault!.StartsWith("Latin1_General_BIN", StringComparison.OrdinalIgnoreCase) ? "SQL_Latin1_General_CP1_CI_AS" : "Latin1_General_BIN2";
            RunTableQuenchProc(cmd, Json(table, other));
            Assert.That(Scalar($"SELECT collation_name FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'Code'"), Is.EqualTo(other),
                "a different collation is still applied");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }
}
