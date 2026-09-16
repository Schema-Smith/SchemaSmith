// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// #242, the remaining expression surfaces on SQL Server: index filter expressions and column defaults.
/// <para><b>These tests measure before anything is wired.</b> The design orders this work by measured pain, and
/// two surfaces it listed turned out not to churn at all once tested — claiming a fix for a surface that was
/// already idempotent would be a lie in the release notes. So each case here is authored in natural form and
/// asserts the object is left alone; whichever of them reddens is a real defect, and only those get wired.</para>
/// <para>Detection is the change audit rather than a text match: SchemaSmith writes a row when it creates or
/// drops an object, so a re-created index or column shows up there whatever the log says.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class TableQuench_ExpressionSurfaceMeasurementTests : BaseTableQuenchTests
{
    private static string FilteredIndexJson(string table, string filter) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                {"Name": "[Status]", "DataType": "INT", "Nullable": true}
            ],
            "Indexes": [
                {"Name": "[IX_{{table}}_Status]", "IndexColumns": "[Status]", "FilterExpression": "{{filter}}"}
            ]
        }
        """;

    private static string DefaultJson(string table, string defaultValue) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                {"Name": "[Qty]", "DataType": "INT", "Nullable": true, "Default": "{{defaultValue}}"}
            ]
        }
        """;

    private static int AuditRowCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit
                              WHERE ObjectName LIKE '%{table}%' AND ActionType IN ('created', 'dropped', 'modified')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ClearAudit(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%{table}%'";
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
            cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}]";
            cmd.ExecuteNonQuery();
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}]";
            cmd.ExecuteNonQuery();
            ClearAudit(cmd, table);
            cmd.CommandText = $"DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}'";
            cmd.ExecuteNonQuery();
        }
    }

    // A filter authored the way a person writes it. SQL Server stores ([Status]>(0)); the index comparison
    // rebuilds the whole CREATE INDEX string from the catalog and compares it to the declared one, so the
    // filter's stored form is part of that string.
    [Test]
    public void AFilteredIndexAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfIdx_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredIndexJson(table, "Status > 0");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd, table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(AuditRowCount(cmd, table), Is.Zero,
                    $"pass {pass}: the filtered index was re-created for an unchanged declaration");
            }
        });
    }

    // A default authored as a bare literal. SQL Server stores ((0)).
    [Test]
    public void AColumnDefaultAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfDef_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL)", cmd =>
        {
            var json = DefaultJson(table, "0");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd, table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(AuditRowCount(cmd, table), Is.Zero,
                    $"pass {pass}: the column default was re-created for an unchanged declaration");
            }
        });
    }

    // A default with a function call, which SQL Server reframes more aggressively than a literal.
    [Test]
    public void AFunctionDefaultAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfFn_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL)", cmd =>
        {
            var json = DefaultJson(table, "datepart(year, getdate())");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd, table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(AuditRowCount(cmd, table), Is.Zero,
                    $"pass {pass}: the function default was re-created for an unchanged declaration");
            }
        });
    }
}
