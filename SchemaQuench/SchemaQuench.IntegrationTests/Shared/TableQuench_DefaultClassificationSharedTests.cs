// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A default is a literal or an expression, decided by the catalog, never by its text (#520). An expression default
/// authored in natural form deploys once and then emits no column DDL; a quoted literal that happens to contain a
/// parenthesis stays a literal; and the delivered row holds the expression's value, not its text.
/// <para>The arbiter of a re-apply is the deploy's own status log, as the other MySQL-family idempotency fixtures use:
/// a converged table must emit no <c>MODIFY COLUMN</c> on the second and third passes.</para>
/// </summary>
public abstract class TableQuench_DefaultClassificationSharedTests : BaseTableQuenchTests
{
    private static string Json(string table) => $$"""
        [
        {
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "EConcat", "DataType": "VARCHAR(40)", "Nullable": true, "Default": "(CONCAT('A','B'))" },
                { "Name": "ELower", "DataType": "VARCHAR(40)", "Nullable": true, "Default": "(LOWER('ABC'))" },
                { "Name": "EArith", "DataType": "INT", "Nullable": true, "Default": "(1 + 1)" },
                { "Name": "SParen", "DataType": "VARCHAR(40)", "Nullable": true, "Default": "'n/a (unknown)'" },
                { "Name": "TStamp", "DataType": "DATETIME", "Nullable": true, "Default": "CURRENT_TIMESTAMP" }
            ],
            "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
            ]
        }
        ]
        """;

    private static void ClearMessages(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private static int ColumnDdlFor(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%MODIFY COLUMN%' AND Message LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string LiveDefault(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $"SELECT CONCAT(IFNULL(COLUMN_DEFAULT, '<null>'), '|', EXTRA) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string DeliveredRow(IDbCommand cmd, string table, int id)
    {
        cmd.CommandText = $"INSERT INTO `{table}` (Id) VALUES ({id}); SELECT CONCAT(EConcat, '|', ELower, '|', EArith, '|', SParen) FROM `{table}` WHERE Id = {id}";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT SchemaSmith_SupportsDefaultExpression()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) Assert.Ignore("Expression defaults need MySQL 8.0.13; skipped below it.");
        try
        {
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ExpressionAndLiteralDefaults_DeployOnce_ThenEmitNoColumnDdl_AndDeliverTheirValues()
    {
        const string table = "DefaultKindsOnce";
        WithTable(table, cmd =>
        {
            RetryingTransientConcurrency(() => RunTableQuenchProc(cmd, Json(table)));
            Assert.Multiple(() =>
            {
                Assert.That(DeliveredRow(cmd, table, 1), Is.EqualTo("AB|abc|2|n/a (unknown)"), "the row holds each default's value: an expression evaluated, a literal verbatim");
                Assert.That(LiveDefault(cmd, table, "SParen"), Does.Not.Contain("DEFAULT_GENERATED"), "a quoted literal with a parenthesis is a literal, not an expression: " + LiveDefault(cmd, table, "SParen"));
                Assert.That(LiveDefault(cmd, table, "EConcat").ToLowerInvariant(), Does.Contain("concat("), "the expression is the column's default: " + LiveDefault(cmd, table, "EConcat"));
            });
            for (var pass = 2; pass <= 3; pass++)
            {
                ClearMessages(cmd);
                RetryingTransientConcurrency(() => RunTableQuenchProc(cmd, Json(table)));
                Assert.That(ColumnDdlFor(cmd, table), Is.Zero, $"pass {pass}: a column was re-altered for an unchanged default");
            }
            Assert.That(DeliveredRow(cmd, table, 2), Is.EqualTo("AB|abc|2|n/a (unknown)"), "the values are the same after the repeat deploys");
        });
    }

    [Test]
    public void ARealDriftInAnExpressionDefault_IsStillCorrected()
    {
        const string table = "DefaultKindsDrift";
        WithTable(table, cmd =>
        {
            RetryingTransientConcurrency(() => RunTableQuenchProc(cmd, Json(table)));
            ClearMessages(cmd);
            RetryingTransientConcurrency(() => RunTableQuenchProc(cmd, Json(table)));
            Assert.That(ColumnDdlFor(cmd, table), Is.Zero, "setup: the second deploy must already be a no-op");

            cmd.CommandText = $"ALTER TABLE `{table}` MODIFY COLUMN `EConcat` VARCHAR(40) NULL DEFAULT 'drifted'";
            cmd.ExecuteNonQuery();
            Assert.That(DeliveredRow(cmd, table, 1), Does.StartWith("drifted|"), "setup: the drift is in place");

            ClearMessages(cmd);
            RetryingTransientConcurrency(() => RunTableQuenchProc(cmd, Json(table)));
            Assert.Multiple(() =>
            {
                Assert.That(ColumnDdlFor(cmd, table), Is.EqualTo(1), "the drifted column, and only it, is altered back");
                Assert.That(DeliveredRow(cmd, table, 2), Is.EqualTo("AB|abc|2|n/a (unknown)"), "the declared expression is the default again");
            });
            ClearMessages(cmd);
            RetryingTransientConcurrency(() => RunTableQuenchProc(cmd, Json(table)));
            Assert.That(ColumnDdlFor(cmd, table), Is.Zero, "and the deploy after the correction is a no-op again");
        });
    }
}
