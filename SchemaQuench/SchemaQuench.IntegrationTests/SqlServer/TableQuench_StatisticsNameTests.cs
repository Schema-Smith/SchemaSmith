// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A user statistic is told from an auto-created one by the catalog's <c>user_created</c> / <c>auto_created</c> flags,
/// never by its name: a statistic a user named <c>stat_…</c> or <c>hind_…</c> deploys once, redeploys as a no-op, and
/// is dropped when the package stops declaring it, while an auto-created statistic on the same table is never touched.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_StatisticsNameTests : BaseTableQuenchTests
{
    private static string Json(string table, bool withStatistics) => $$"""
        [{
            "Schema": "dbo",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": true },
                { "Name": "Other", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "PK_{{table}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
            ]{{(withStatistics ? ",\n    \"Statistics\": [ { \"Name\": \"stat_" + table + "_Code\", \"Columns\": \"[Code]\" }, { \"Name\": \"hind_" + table + "_Other\", \"Columns\": \"[Other]\" } ]" : "")}}
        }]
        """;

    private static string UserStatistics(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT ISNULL(STRING_AGG([name], ',') WITHIN GROUP (ORDER BY [name]), '') FROM sys.stats WHERE [object_id] = OBJECT_ID('dbo.{table}') AND user_created = 1";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static int AutoStatistics(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.stats WHERE [object_id] = OBJECT_ID('dbo.{table}') AND auto_created = 1";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int StatisticAudits(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%' AND ObjectType = 'statistic'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Test]
    public void AStatisticNamedLikeAnAutoCreatedOne_DeploysOnce_AndIsDroppedWhenRemoved_WhileAutoStatisticsStay()
    {
        const string table = "StatNames";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, withStatistics: true));
            Assert.That(UserStatistics(cmd, table), Is.EqualTo($"hind_{table}_Other,stat_{table}_Code"), "both declared statistics exist");

            // A predicate on a column with no statistic makes the optimizer create one (auto_created = 1).
            cmd.CommandText = $"INSERT INTO dbo.{table} (Id, Code, Other) VALUES (1, 'A', 1), (2, 'B', 2); SELECT COUNT(*) FROM dbo.{table} WHERE Id > 0 AND Other = 1 AND Code = 'A'";
            cmd.ExecuteScalar();

            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table, withStatistics: true));
            Assert.Multiple(() =>
            {
                Assert.That(StatisticAudits(cmd, table), Is.Zero, "a redeploy of the same statistics changes nothing");
                Assert.That(UserStatistics(cmd, table), Is.EqualTo($"hind_{table}_Other,stat_{table}_Code"));
            });

            var autoBefore = AutoStatistics(cmd, table);
            RunTableQuenchProc(cmd, Json(table, withStatistics: false));
            Assert.Multiple(() =>
            {
                Assert.That(UserStatistics(cmd, table), Is.Empty, "the statistics the package no longer declares are dropped, whatever their names");
                Assert.That(AutoStatistics(cmd, table), Is.EqualTo(autoBefore), "the engine's own statistics are left alone");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }
}
