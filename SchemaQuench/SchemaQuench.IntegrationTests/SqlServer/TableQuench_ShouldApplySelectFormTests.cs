// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// #282: a component ShouldApplyExpression may be written in the folder-gate form (a projection-only
/// SELECT) as well as the bare-predicate form. The engine strips a leading SELECT before embedding
/// the predicate in NOT (&lt;expr&gt;); without that, a SELECT-form component gate produced SQL Server
/// Msg 4145 ("expression of non-boolean type ... where a condition is expected"). Verifies the
/// SELECT form is accepted and applies-when-true / skips-when-false on a table gate and column gates.
/// (Index/other component gates embed through the identical fn_StripLeadingSelect wrap.)
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ShouldApplySelectFormTests : BaseTableQuenchTests
{
    [Test]
    public void SelectFormGate_OnComponents_AppliesWhenTrue_SkipsWhenFalse()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"SelGate_{id}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // A table-level SELECT-form gate that is true (the table is created) plus column-level
            // SELECT-form gates, one true and one false.
            var json = $$"""
            [
              {
                "Schema": "[dbo]",
                "Name": "[{{table}}]",
                "ShouldApplyExpression": "SELECT 1 = 1",
                "Columns": [
                  { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                  { "Name": "[ColYes]", "DataType": "INT", "Nullable": true, "ShouldApplyExpression": "SELECT 1 = 1" },
                  { "Name": "[ColNo]", "DataType": "INT", "Nullable": true, "ShouldApplyExpression": "SELECT 1 = 0" }
                ],
                "Indexes": [
                  { "Name": "[PK_{{table}}]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true, "Clustered": true }
                ]
              }
            ]
            """;
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json),
                "A SELECT-form component gate must not raise Msg 4145 (#282).");

            Assert.Multiple(() =>
            {
                Assert.That(ObjectExists(cmd, table), Is.True, "Table (true SELECT-form gate) should be created.");
                Assert.That(ColumnExists(cmd, table, "ColYes"), Is.True, "Column with a true SELECT-form gate should be created.");
                Assert.That(ColumnExists(cmd, table, "ColNo"), Is.False, "Column with a false SELECT-form gate should be skipped.");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static bool ObjectExists(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.[{table}]', 'U') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static bool ColumnExists(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.columns WITH (NOLOCK) WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = '{column}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }
}
