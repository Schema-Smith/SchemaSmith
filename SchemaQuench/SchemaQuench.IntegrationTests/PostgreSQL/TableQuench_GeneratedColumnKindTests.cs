// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// PG-044: a generated column's STORED / VIRTUAL kind was read from the catalog and compared nowhere, so a package that
/// switched it deployed at exit 0 and left the old kind. PostgreSQL has no ALTER for it; the column is re-created.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_GeneratedColumnKindTests : BaseTableQuenchTests
{
    [Test]
    public void AGeneratedColumnSwitchedBetweenStoredAndVirtual_TakesTheDeclaredKind()
    {
        var table = $"genkind_{Guid.NewGuid():N}"[..16];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT current_setting('server_version_num')::int";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 180000)
            Assert.Ignore("VIRTUAL generated columns are PostgreSQL 18.");

        string Json(bool isVirtual) => $$"""
            [{ "Schema": "public", "Name": "{{table}}",
               "Columns": [ { "Name": "a", "DataType": "integer", "Nullable": true },
                            { "Name": "g", "DataType": "integer", "Nullable": true, "Generated": "ALWAYS", "GenerationExpression": "a * 2", "Virtual": {{(isVirtual ? "true" : "false")}} } ] }]
            """;
        string Kind()
        {
            cmd.CommandText = $"SELECT attgenerated::text FROM pg_attribute WHERE attrelid = 'public.{table}'::regclass AND attname = 'g' AND NOT attisdropped";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        int Audits()
        {
            cmd.CommandText = $"""SELECT COUNT(*) FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{table}.g'""";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        void Deploy(bool isVirtual)
        {
            cmd.CommandText = """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(isVirtual));
        }
        try
        {
            Deploy(false);
            Assert.That(Kind(), Is.EqualTo("s"), "precondition");
            Deploy(true);
            Assert.That(Kind(), Is.EqualTo("v"), "STORED became VIRTUAL");
            Deploy(true);
            Assert.That(Audits(), Is.Zero, "then a redeploy is a no-op");
            Deploy(false);
            Assert.That(Kind(), Is.EqualTo("s"), "and back");
            cmd.CommandText = $"INSERT INTO public.{table} (a) VALUES (21) RETURNING g";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(42), "computing as declared");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }
}
