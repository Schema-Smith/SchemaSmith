// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class UnchangedModuleSkipTests : UnchangedModuleSkipSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";

    protected override string TableJson => """
        { "Name": "unchanged_t", "Columns": [ { "Name": "x", "DataType": "integer", "Nullable": false }, { "Name": "w", "DataType": "integer", "Nullable": true } ],
          "Indexes": [ { "Name": "pk_unchanged_t", "PrimaryKey": true, "Unique": true, "IndexColumns": "x" } ] }
        """;

    protected override Dictionary<string, string> Scripts => new()
    {
        ["Views"] = "CREATE OR REPLACE VIEW public.v_unchanged AS\n SELECT x FROM public.unchanged_t;",
        ["Functions"] = "CREATE OR REPLACE FUNCTION public.f_unchanged(a integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT a + 1 $$;",
        ["Procedures"] = "-- Call it from a job, e.g. CREATE VIEW v_doc AS SELECT 1\n/* CREATE FUNCTION f_doc() ... */\nCREATE OR REPLACE PROCEDURE public.p_unchanged() LANGUAGE sql AS $$ SELECT 1 $$;",
        ["Triggers"] = "DROP TRIGGER IF EXISTS tr_unchanged ON public.unchanged_t;\nCREATE TRIGGER tr_unchanged BEFORE INSERT ON public.unchanged_t FOR EACH ROW EXECUTE FUNCTION public.tf_unchanged();"
    };

    protected override string AlterViewByHand => "CREATE OR REPLACE VIEW public.v_unchanged AS SELECT x FROM public.unchanged_t WHERE x > 0";
    protected override string ChangedFunctionScript => "CREATE OR REPLACE FUNCTION public.f_unchanged(a integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT a + 2 $$;";
    protected override string[] DropStatements =>
    [
        "DROP TRIGGER IF EXISTS tr_unchanged ON public.unchanged_t", "DROP VIEW IF EXISTS public.v_unchanged", "DROP FUNCTION IF EXISTS public.f_unchanged(integer)",
        "DROP PROCEDURE IF EXISTS public.p_unchanged()", "DROP TABLE IF EXISTS public.unchanged_t",
        "CREATE OR REPLACE FUNCTION public.tf_unchanged() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$"
    ];
}
