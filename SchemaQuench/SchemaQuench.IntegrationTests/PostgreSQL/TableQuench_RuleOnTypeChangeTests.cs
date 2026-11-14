// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A user rule on a table that reads a column whose type changes has to come off first. The rule's table was read as a
// view, so the pass emitted DROP VIEW against the table (42809) and the deploy stopped; the rule arm was unreachable and
// would have failed anyway, written without the ON <table> that DROP RULE needs.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_RuleOnTypeChangeTests : BaseTableQuenchTests
{
    private static string Json(string table, string type) => $$"""
        {
            "Schema": "public",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "code", "DataType": "{{type}}", "Nullable": true }
            ],
            "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" } ]
        }
        """;

    [Test]
    public void AUserRuleReadingATypeChangingColumn_IsDropped_AndTheColumnChanges()
    {
        var table = $"rule_tc_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, "varchar(10)"));
            cmd.CommandText = $"CREATE RULE r_{table} AS ON UPDATE TO public.{table} WHERE NEW.code = 'x' DO INSTEAD NOTHING";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, "varchar(20)")));

            cmd.CommandText = $"SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.{table}'::regclass AND attname = 'code'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("character varying(20)"));
            cmd.CommandText = $"SELECT COUNT(*) FROM pg_rewrite WHERE ev_class = 'public.{table}'::regclass AND rulename = 'r_{table}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "the undeclared rule came off so the type could change");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }
}
