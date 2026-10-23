// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// #282: a component ShouldApplyExpression may be written in the folder-gate form (a projection-only
/// SELECT) as well as the bare-predicate form. The engine strips a leading SELECT before embedding
/// the predicate in NOT (&lt;expr&gt;); without that, a SELECT-form component gate broke the generated
/// dynamic SQL. Verifies the SELECT form is accepted and applies-when-true / skips-when-false on a
/// table gate and column gates. (Index/other component gates embed through the identical
/// "SchemaSmith"."StripLeadingSelect" wrap.)
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ShouldApplySelectFormTests : BaseTableQuenchTests
{
    [Test]
    public void SelectFormGate_OnComponents_AppliesWhenTrue_SkipsWhenFalse()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var table = $"SelGate_{id}";
        var product = $"SelGateProduct_{id}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var json = $$"""
            [
              {
                "Schema": "{{schema}}",
                "Name": "{{table}}",
                "ShouldApplyExpression": "SELECT 1 = 1",
                "Columns": [
                  { "Name": "Id", "DataType": "integer", "Nullable": false },
                  { "Name": "ColYes", "DataType": "integer", "Nullable": true, "ShouldApplyExpression": "SELECT 1 = 1" },
                  { "Name": "ColNo", "DataType": "integer", "Nullable": true, "ShouldApplyExpression": "SELECT 1 = 0" }
                ],
                "Indexes": [
                  { "Name": "PK_{{table}}", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true }
                ]
              }
            ]
            """;
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product),
                "A SELECT-form component gate must not break the generated dynamic SQL (#282).");

            Assert.Multiple(() =>
            {
                Assert.That(ObjectExists(cmd, schema, table), Is.True, "Table (true SELECT-form gate) should be created.");
                Assert.That(ColumnExists(cmd, schema, table, "ColYes"), Is.True, "Column with a true SELECT-form gate should be created.");
                Assert.That(ColumnExists(cmd, schema, table, "ColNo"), Is.False, "Column with a false SELECT-form gate should be skipped.");
            });
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{table}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static bool ObjectExists(IDbCommand cmd, string schema, string tableName)
    {
        cmd.CommandText = $"SELECT to_regclass('\"{schema}\".\"{tableName}\"') IS NOT NULL";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static bool ColumnExists(IDbCommand cmd, string schema, string tableName, string column)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{tableName}' AND column_name = '{column}'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
