// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A <c>VECTOR(n)</c> column (SQL Server 2025) renders with its dimension count, and <c>VECTOR(n, float16)</c> with its
/// base precision, so a deployed vector column compares equal to its declaration and a redeploy leaves it alone; a
/// changed dimension count is a real change and is applied. Skipped below 2025, where the type does not exist.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_VectorColumnTests : BaseTableQuenchTests
{
    private static string Json(string table, string embDims, string halfDims) => $$"""
        [{
            "Schema": "dbo",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Emb", "DataType": "VECTOR({{embDims}})", "Nullable": true },
                { "Name": "Half", "DataType": "VECTOR({{halfDims}}, float16)", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "PK_{{table}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
            ]
        }]
        """;

    private static int ColumnAudits(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'column' AND ObjectName LIKE 'dbo.{table}.%' AND ActionType = 'modified'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Test]
    public void AVectorColumn_RedeploysAsANoOp_AndAChangedDimensionCountIsApplied()
    {
        const string table = "VectorColumns";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 17) Assert.Ignore("VECTOR is a SQL Server 2025 type.");
        cmd.CommandText = "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON";
        cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(cmd, Json(table, "3", "4"));
            Assert.Multiple(() =>
            {
                Assert.That(GetColumnDataType(cmd, table, "Emb"), Is.EqualTo("VECTOR(3)"));
                Assert.That(GetColumnDataType(cmd, table, "Half"), Is.EqualTo("VECTOR(4, FLOAT16)"));
            });
            cmd.CommandText = $"INSERT INTO dbo.{table} (Id, Emb, Half) VALUES (1, '[1,2,3]', '[1,2,3,4]')";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table, "3", "4"));
            Assert.That(ColumnAudits(cmd, table), Is.Zero, "a redeploy of the same vector columns alters nothing");
            cmd.CommandText = $"SELECT CAST(JSON_VALUE(CAST(Emb AS NVARCHAR(MAX)), '$[2]') AS FLOAT) FROM dbo.{table} WHERE Id = 1";
            Assert.That(Convert.ToDouble(cmd.ExecuteScalar()), Is.EqualTo(3d), "the row survived");

            // A changed dimension count is a real change, and one the engine refuses to make in place even on an empty
            // table ("The vector dimensions 3 and 5 do not match"): the deploy reports the engine's refusal rather than
            // pretending the column converged.
            cmd.CommandText = $"DELETE FROM dbo.{table}";
            cmd.ExecuteNonQuery();
            Assert.That(() => RunTableQuenchProc(cmd, Json(table, "5", "4")), Throws.Exception.With.Message.Contains("vector dimensions 3 and 5 do not match"));
            Assert.That(GetColumnDataType(cmd, table, "Emb"), Is.EqualTo("VECTOR(3)"), "the column is as it was");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }
}
