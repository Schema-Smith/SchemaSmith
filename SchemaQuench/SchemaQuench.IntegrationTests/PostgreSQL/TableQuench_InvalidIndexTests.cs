// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An index the engine marks invalid (a <c>CREATE INDEX CONCURRENTLY</c> that failed leaves one behind, by name, that
/// no query uses) is not the declared index: the deploy drops it and creates the declared one, which is valid.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_InvalidIndexTests : BaseTableQuenchTests
{
    private const string Json = """
        {
            "Schema": "public",
            "Name": "invalid_idx",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "n", "DataType": "integer", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "pk_invalid_idx", "PrimaryKey": true, "IndexColumns": "id" },
                { "Name": "ux_invalid_idx_n", "Unique": true, "IndexColumns": "n" }
            ]
        }
        """;

    [Test]
    public void AnInvalidIndex_IsDroppedAndCreatedAgain_Valid()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = "CREATE TABLE public.invalid_idx (id integer NOT NULL CONSTRAINT pk_invalid_idx PRIMARY KEY, n integer); INSERT INTO public.invalid_idx VALUES (1, 7), (2, 7)";
            cmd.ExecuteNonQuery();
            // The duplicate makes the concurrent unique build fail, and a failed concurrent build leaves an invalid index behind.
            cmd.CommandText = "CREATE UNIQUE INDEX CONCURRENTLY ux_invalid_idx_n ON public.invalid_idx (n)";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception, "setup: the concurrent build must fail");
            Assert.That(IndexState(cmd), Does.EndWith("|false"), "setup: the index is left behind invalid: " + IndexState(cmd));
            var before = IndexState(cmd);
            cmd.CommandText = "DELETE FROM public.invalid_idx WHERE id = 2";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json);
            var after = IndexState(cmd);
            Assert.Multiple(() =>
            {
                Assert.That(after, Does.EndWith("|true"), "the declared index is valid: " + after);
                Assert.That(after.Split('|')[0], Is.Not.EqualTo(before.Split('|')[0]), "it is a new index, not the invalid one renamed");
            });
            cmd.CommandText = "INSERT INTO public.invalid_idx VALUES (3, 7)";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception.With.Message.Contains("duplicate key"), "and it enforces uniqueness");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS public.invalid_idx";
            cmd.ExecuteNonQuery();
        }
    }

    private static string IndexState(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = "SELECT i.oid::text || '|' || idx.indisvalid::text FROM pg_class i JOIN pg_index idx ON idx.indexrelid = i.oid WHERE i.relname = 'ux_invalid_idx_n'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
}
