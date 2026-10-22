// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// The index-only path on a table whose names hold a double quote: the indexes and statistics are created, re-applied as
/// unchanged, and changed, under their own names.
/// </summary>
[Category("PostgreSQL")]
[NonParallelizable]
public class IndexOnlyQuotedIdentifierTests : BaseTableQuenchTests
{
    [Test]
    public void IndexesAndStatisticsOnATableWhoseNamesHoldADoubleQuote_AreAppliedByTheIndexOnlyPath()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Exec(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        long Count(string sql) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
        string Json(string filter) => $$"""
            [{ "Schema": "public", "Name": "Io\"t",
               "Indexes": [ { "Name": "ix\"io", "IndexColumns": "\"La\"\"bel\"", "FilterExpression": "{{filter}}" },
                            { "Name": "ux\"io", "Unique": true, "IndexColumns": "\"I\"\"d\"" } ],
               "Statistics": [ { "Name": "st\"io", "StatisticsColumns": "\"La\"\"bel\", \"I\"\"d\"" } ] }]
            """;
        Exec("DROP TABLE IF EXISTS public.\"Io\"\"t\"; CREATE TABLE public.\"Io\"\"t\" (\"I\"\"d\" integer NOT NULL, \"La\"\"bel\" text)");
        try
        {
            RunTableQuenchProc(cmd, Json("\\\"I\\\"\\\"d\\\" > 0"), indexOnly: true);
            Assert.That(Count("SELECT count(*) FROM pg_class WHERE relname IN ('ix\"io', 'ux\"io') AND relkind = 'i'"), Is.EqualTo(2), "both indexes");
            Assert.That(Count("SELECT count(*) FROM pg_statistic_ext WHERE stxname = 'st\"io'"), Is.EqualTo(1), "and the statistics");
            cmd.CommandText = "SELECT oid FROM pg_class WHERE relname = 'ix\"io'";
            var before = cmd.ExecuteScalar();

            RunTableQuenchProc(cmd, Json("\\\"I\\\"\\\"d\\\" > 0"), indexOnly: true);
            cmd.CommandText = "SELECT oid FROM pg_class WHERE relname = 'ix\"io'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(before), "re-applying leaves the index as it is");

            RunTableQuenchProc(cmd, Json("\\\"I\\\"\\\"d\\\" > 10"), indexOnly: true);
            cmd.CommandText = "SELECT pg_get_expr(indpred, indrelid) FROM pg_index WHERE indexrelid = 'public.\"ix\"\"io\"'::regclass";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("> 10"), "and a change is applied");
        }
        finally
        {
            Exec("DROP TABLE IF EXISTS public.\"Io\"\"t\"");
        }
    }
}
