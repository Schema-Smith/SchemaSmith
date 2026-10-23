// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An extended-statistics object's target (<c>ALTER STATISTICS … SET STATISTICS n</c>, PostgreSQL 13) is declared
/// state: applied when the object is created, converged in place when it changes, and left alone when the package
/// says nothing. Below 13 a declared target follows the unsupported-feature policy.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_StatisticsTargetTests : BaseTableQuenchTests
{
    private static string Json(string table, string target) => $$"""
        {
            "Schema": "public",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "a", "DataType": "integer", "Nullable": true },
                { "Name": "b", "DataType": "integer", "Nullable": true }
            ],
            "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" } ],
            "Statistics": [ { "Name": "st_{{table}}", "Kind": "NDISTINCT", "StatisticsColumns": "a,b"{{target}} } ]
        }
        """;

    private static string StatState(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT se.oid::text || '|' || COALESCE(NULLIF((to_jsonb(se)->>'stxstattarget')::int, -1)::text, '-') FROM pg_statistic_ext se WHERE se.stxname = 'st_{table}'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static int Downgrades(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ActionType\" = 'downgraded' AND \"ObjectName\" = 'public.{table}.st_{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ADeclaredTarget_IsApplied_ConvergesInPlace_AndRedeploysAsANoOp()
    {
        const string table = "stat_target";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, ", \"StatisticsTarget\": 500"));
            var first = StatState(cmd, table);
            if (PgServerMajor() < 13)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(first, Does.EndWith("|-"), "below 13 the object is created without a target: " + first);
                    Assert.That(Downgrades(cmd, table), Is.EqualTo(1), "and the downgrade is recorded");
                });
                return;
            }
            Assert.That(first, Does.EndWith("|500"), first);

            RunTableQuenchProc(cmd, Json(table, ", \"StatisticsTarget\": 500"));
            Assert.That(StatState(cmd, table), Is.EqualTo(first), "the redeploy neither dropped nor altered the object");

            RunTableQuenchProc(cmd, Json(table, ", \"StatisticsTarget\": 300"));
            var second = StatState(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(second, Does.EndWith("|300"), second);
                Assert.That(second.Split('|')[0], Is.EqualTo(first.Split('|')[0]), "a changed target is an ALTER, not a drop and re-create");
            });
        });
    }

    [Test]
    public void AnUndeclaredTarget_LeavesAHandSetOneAlone()
    {
        const string table = "stat_target_unset";
        WithTable(table, cmd =>
        {
            if (PgServerMajor() < 13) Assert.Ignore("The statistics target is PostgreSQL 13.");
            RunTableQuenchProc(cmd, Json(table, ""));
            cmd.CommandText = $"ALTER STATISTICS public.st_{table} SET STATISTICS 250";
            cmd.ExecuteNonQuery();
            var before = StatState(cmd, table);
            Assert.That(before, Does.EndWith("|250"));
            RunTableQuenchProc(cmd, Json(table, ""));
            Assert.That(StatState(cmd, table), Is.EqualTo(before), "unset means unmanaged");
        });
    }
}
