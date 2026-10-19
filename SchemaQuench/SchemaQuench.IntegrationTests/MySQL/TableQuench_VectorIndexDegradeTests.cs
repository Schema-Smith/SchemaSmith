// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

// MySQL 9's Community server has VECTOR columns but no vector index. A declared one is skipped with a downgrade, and the
// table deploys; under the fail policy the deploy is refused by name instead.
[Category("MySQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_VectorIndexDegradeTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    private static string Json(string table) => $$"""
        [{
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "INT", "Nullable": false },
                { "Name": "v", "DataType": "VECTOR(3)", "Nullable": false }
            ],
            "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" },
                { "Name": "vx", "IndexType": "VECTOR", "IndexColumns": "`v`", "Distance": "cosine" }
            ]
        }]
        """;

    private void WithVectorColumns(Action<IDbCommand, string> body)
    {
        var table = $"VecSkip_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"USE `{_mainDb}`";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $"CREATE TABLE `{table}_probe` (v VECTOR(3))";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP TABLE `{table}_probe`";
            cmd.ExecuteNonQuery();
        }
        catch (DbException)
        {
            Assert.Ignore("VECTOR columns arrived in MySQL 9.0.");
        }
        try
        {
            body(cmd, table);
        }
        finally
        {
            cmd.CommandText = $"SET @schemasmith_unsupported_policy = NULL; DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ADeclaredVectorIndex_IsSkippedWithADowngrade_AndTheTableDeploys(bool indexOnly)
    {
        WithVectorColumns((cmd, table) =>
        {
            if (indexOnly)
            {
                cmd.CommandText = $"CREATE TABLE `{table}` (id INT NOT NULL PRIMARY KEY, v VECTOR(3) NOT NULL)";
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table), indexOnly);
            Assert.Multiple(() =>
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'v'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the table deploys with its vector column");
                cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'vx'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "and no index stands in for the vector one");
                cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ActionType = 'downgraded' AND ObjectType = 'INDEX (VECTOR, MariaDB 11.7)' AND ObjectName = '{table}.vx'";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the downgrade is recorded");
            });

            cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table), indexOnly);
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'index'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "a redeploy changes no index");
        });
    }

    [Test]
    public void UnderTheFailPolicy_ADeclaredVectorIndexIsRefusedByName()
    {
        WithVectorColumns((cmd, table) =>
        {
            cmd.CommandText = "SET @schemasmith_unsupported_policy = 'fail'";
            cmd.ExecuteNonQuery();
            var refusal = Assert.Throws(Is.InstanceOf<DbException>(), () => RunTableQuenchProc(cmd, Json(table)));
            Assert.That(refusal!.Message, Does.Contain("VECTOR index").And.Contain($"{table}.vx"));
        });
    }
}
