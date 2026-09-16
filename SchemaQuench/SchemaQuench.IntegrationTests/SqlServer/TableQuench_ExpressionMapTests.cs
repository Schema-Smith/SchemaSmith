// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Expression false-change detection (#242) on SQL Server: an expression the engine rewrote on storage must
/// stop reading as a change on the next deploy.
/// <para><b>Every expression here is authored in natural form</b> — the way a person writes it, not the way
/// SQL Server stores it. A test authored in canonical form is the bug: it proves the tool agrees with itself
/// and nothing more, which is how this whole family survived a green suite.</para>
/// <para><b>The assertions are identity, not text.</b> A check constraint keeps its <c>object_id</c> and a
/// computed column its <c>column_id</c> only if it was left alone; a drop-and-recreate changes both. Counting
/// rows or matching log text would pass while the object churned underneath.</para>
/// <para><b>Both directions are covered on purpose.</b> Going quiet on churn is only half the job — a
/// metadata-only compare would also leave a hand-edited live expression in place forever, which is a worse
/// failure than the churn. The drift test is the one that pins that.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class TableQuench_ExpressionMapTests : BaseTableQuenchTests
{
    private static string CheckJson(string table, string expression) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[RetentionDays]", "DataType": "INT", "Nullable": true}
            ],
            "CheckConstraints": [
                {"Name": "[CK_{{table}}_Retention]", "Expression": "{{expression}}"}
            ]
        }
        """;

    private static string ComputedJson(string table, string expression) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Qty]", "DataType": "INT", "Nullable": false},
                {"Name": "[Doubled]", "DataType": "INT", "Nullable": true, "ComputedExpression": "{{expression}}"}
            ]
        }
        """;

    private static int ConstraintObjectId(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ISNULL((SELECT ck.[object_id] FROM sys.check_constraints ck
                                             WHERE ck.parent_object_id = OBJECT_ID('dbo.{table}')), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int ComputedColumnId(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ISNULL((SELECT c.column_id FROM sys.computed_columns c
                                             WHERE c.[object_id] = OBJECT_ID('dbo.{table}') AND c.[name] = 'Doubled'), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string LiveCheckDefinition(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ck.[definition] FROM sys.check_constraints ck
                              WHERE ck.parent_object_id = OBJECT_ID('dbo.{table}')";
        return cmd.ExecuteScalar() as string;
    }

    private static int MapRowCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Drop(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}]";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}'";
        cmd.ExecuteNonQuery();
    }

    private void WithTable(string table, string createSql, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            Drop(cmd, table);
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
            body(cmd);
        }
        finally { Drop(cmd, table); }
    }

    // Authored without bracket-qualifying the column, which is how people write it. SQL Server stores
    // ([RetentionDays]<=(365)); the normalizer undoes the parens and spacing but not the brackets it added,
    // so the texts never match and the constraint was dropped and re-created on every deploy.
    [Test]
    public void ACheckAuthoredWithoutBrackets_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprMapChk_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);
            var firstId = ConstraintObjectId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the constraint must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(ConstraintObjectId(cmd, table), Is.EqualTo(firstId),
                    $"pass {pass}: the constraint was dropped and re-created, so an unchanged declaration is "
                    + "still reading as a change");
            }
        });
    }

    // Computed columns have no normalization at all: authored Qty * 2 is stored as ([Qty]*(2)), so the column
    // was dropped and re-added on every deploy -- which on a PERSISTED column is a table-rewriting operation.
    [Test]
    public void AComputedColumnAuthoredInNaturalForm_IsNotDroppedAndReAddedOnEveryDeploy()
    {
        var table = $"ExprMapCol_{Guid.NewGuid():N}"[..20];
        // SQL Server reports a non-persisted computed column as NULLABLE whatever its source column says, so
        // the declaration says Nullable: true. Getting that wrong churns the column on a nullability mismatch --
        // a real difference, but not the one under test here.
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Qty] INT NOT NULL)", cmd =>
        {
            var json = ComputedJson(table, "Qty * 2");
            RunTableQuenchProc(cmd, json);
            var firstId = ComputedColumnId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the computed column must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(ComputedColumnId(cmd, table), Is.EqualTo(firstId),
                    $"pass {pass}: the computed column was dropped and re-added for an unchanged declaration");
            }
        });
    }

    // The load-bearing half. Someone edits the constraint in SSMS; the package did not change. A metadata-only
    // compare would go quiet and leave the hand-edited expression in place forever.
    [Test]
    public void AnOutOfBandEdit_IsStillReApplied()
    {
        var table = $"ExprMapDrift_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $@"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [CK_{table}_Retention];
                                 ALTER TABLE dbo.[{table}] ADD CONSTRAINT [CK_{table}_Retention] CHECK ([RetentionDays] <= 999)";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, json);

            Assert.That(LiveCheckDefinition(cmd, table), Does.Contain("365").And.Not.Contain("999"),
                "the live expression drifted from what SchemaSmith applied, so it must be re-applied -- going "
                + "quiet here would silently accept a hand-edited constraint");
        });
    }

    // A mapping is only trustworthy for the context it was written in: SQL Server freezes an expression's
    // stored text at creation-time compatibility level. A row whose context no longer matches is stale, not
    // wrong -- re-read and rewrite it, and do NOT touch the object, which would drop and recreate every
    // expression-bearing object in the database on one deploy.
    [Test]
    public void AStaleContextRow_IsReBaselined_WithoutTouchingTheObject()
    {
        var table = $"ExprMapCtx_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);
            var firstId = ConstraintObjectId(cmd, table);

            cmd.CommandText = $@"UPDATE SchemaSmith.ExpressionMap
                                    SET [EngineVersion] = 'from-another-server', [CanonicalText] = 'stale text'
                                  WHERE [ObjectTable] = '{table}'";
            Assert.That(cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist to go stale");

            RunTableQuenchProc(cmd, json);

            Assert.That(ConstraintObjectId(cmd, table), Is.EqualTo(firstId),
                "a stale context must re-baseline, never re-apply");
            cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap
                                  WHERE [ObjectTable] = '{table}' AND [EngineVersion] = 'from-another-server'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "the stale row must be rewritten with the current context");
        });
    }

    [Test]
    public void ApplyingAnExpression_RecordsWhatWasApplied()
    {
        var table = $"ExprMapRec_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            RunTableQuenchProc(cmd, CheckJson(table, "RetentionDays <= 365"));

            Assert.That(MapRowCount(cmd, table), Is.EqualTo(1), "the applied expression must be recorded");
            cmd.CommandText = $@"SELECT [AuthoredText] + '|' + [CanonicalText] FROM SchemaSmith.ExpressionMap
                                  WHERE [ObjectTable] = '{table}'";
            var row = (string)cmd.ExecuteScalar();
            Assert.That(row, Does.StartWith("RetentionDays <= 365|"), "the authored text is stored as written: " + row);
            Assert.That(row, Does.Contain("[RetentionDays]"), "and the canonical text as the engine reports it: " + row);
        });
    }

    // A change to the declaration must still re-apply: the mapping is an optimisation over a working
    // comparison, never a way to stop noticing a real edit.
    [Test]
    public void AChangedDeclaration_IsStillApplied()
    {
        var table = $"ExprMapChg_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            RunTableQuenchProc(cmd, CheckJson(table, "RetentionDays <= 365"));
            RunTableQuenchProc(cmd, CheckJson(table, "RetentionDays <= 730"));

            Assert.That(LiveCheckDefinition(cmd, table), Does.Contain("730"),
                "an edited declaration must reach the server");
        });
    }

}
