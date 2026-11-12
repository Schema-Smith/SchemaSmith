// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class TableSplitInABeforeScriptTests : TableSplitInABeforeScriptSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "TableSplitProduct";

    protected override string TableFileName(string table) => $"public.{table}.json";
    protected override string TableJson(string table) => $$"""
        { "Schema": "public", "Name": "{{table}}",
          "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
          "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
        """;
    protected override string SplitScript => """
        DO $$
        BEGIN
          IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'vs_customer') THEN
            DROP TABLE public.vs_customer;
            CREATE VIEW public.vs_customer AS SELECT id FROM public.vs_keep;
          END IF;
        END $$;
        """;
    protected override string DropAll => "DROP VIEW IF EXISTS public.vs_customer; DROP TABLE IF EXISTS public.vs_customer; DROP TABLE IF EXISTS public.vs_keep;";
    protected override string ViewCount => "SELECT COUNT(*) FROM information_schema.views WHERE table_schema = 'public' AND table_name = 'vs_customer'";
}
