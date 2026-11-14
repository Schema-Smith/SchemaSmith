// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A7: whether a generated column reads a column whose type changes was a substring test on its expression, so changing
// "a" dropped (CASCADE) and re-added a generated column that reads only "ab", taking its indexes with it. The catalog's
// own record of what the column reads is used instead.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_GeneratedColumnDependencyTests : BaseTableQuenchTests
{
    private static string Json(string table, string aType) => $$"""
        {
            "Schema": "public",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "a", "DataType": "{{aType}}", "Nullable": true },
                { "Name": "ab", "DataType": "integer", "Nullable": true },
                { "Name": "g", "DataType": "integer", "Nullable": true, "GenerationExpression": "ab * 2" }
            ],
            "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" } ]
        }
        """;

    [Test]
    public void ChangingAColumn_LeavesAGeneratedColumnThatOnlyContainsItsName()
    {
        var table = $"gen_dep_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string GeneratedAttnum()
        {
            cmd.CommandText = $"SELECT attnum::text FROM pg_attribute WHERE attrelid = 'public.{table}'::regclass AND attname = 'g'";
            return TransientRetry.Run(() => cmd.ExecuteScalar())?.ToString();
        }
        try
        {
            RunTableQuenchProc(cmd, Json(table, "integer"));
            var before = GeneratedAttnum();
            RunTableQuenchProc(cmd, Json(table, "bigint"));
            Assert.That(GeneratedAttnum(), Is.EqualTo(before), "g was not dropped and re-added");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }
}
