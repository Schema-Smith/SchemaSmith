// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class ModuleDefinitionReaderTests : ModuleDefinitionReaderSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string ConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string Setup => "CREATE TABLE public.mdr_t (id int)\n--\n" +
        "CREATE OR REPLACE FUNCTION public.mdr_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$\n--\n" +
        "CREATE TRIGGER mdr_tr BEFORE INSERT ON public.mdr_t FOR EACH ROW EXECUTE FUNCTION public.mdr_fn()";
    protected override string ChangeTrigger => "ALTER TABLE public.mdr_t DISABLE TRIGGER mdr_tr";
    protected override string Teardown => "DROP TABLE IF EXISTS public.mdr_t\n--\nDROP FUNCTION IF EXISTS public.mdr_fn()";
    protected override ModuleScriptIdentity Trigger => new() { Kind = ModuleKind.Trigger, Schema = "public", Name = "mdr_tr", TableSchema = "public", TableName = "mdr_t" };
}
