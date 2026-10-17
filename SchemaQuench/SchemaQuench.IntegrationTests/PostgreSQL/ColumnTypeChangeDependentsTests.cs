// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A column type change on PostgreSQL drops the views, and from 14 the SQL-standard routines (BEGIN ATOMIC / RETURN),
/// that depend on the column. Three defects met here, all through a real deploy:
/// <list type="bullet">
/// <item>a BEGIN ATOMIC function did not deploy at all: the statement splitter, and then Npgsql, cut it at the semicolons
/// inside its body (42601, "syntax error at end of input");</item>
/// <item>PG-128: a type change under such a function failed the deploy (0A000, "cannot alter type of a column used by a
/// function or procedure");</item>
/// <item>the dropped dependents were not put back in the same run: their scripts had already been applied by the first
/// object pass, so a view was missing until the next deploy.</item>
/// </list>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class ColumnTypeChangeDependentsTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "TypeChangeDependents";

    private const string Drops = "DROP VIEW IF EXISTS public.v_dep; DROP FUNCTION IF EXISTS public.f_dep(); DROP TABLE IF EXISTS public.t_dep";

    private void Write(string type)
    {
        WritePackage("""
            { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" },
            { "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" }
            """, "Functions", "Views");
        WriteFile("Tables", "public.t_dep.json", $$"""
            { "Schema": "public", "Name": "t_dep",
              "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
              "Indexes": [ { "Name": "pk_t_dep", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
            """);
        // Two statements and a CASE ... END inside the body: each semicolon and the inner END used to end the routine early.
        WriteFile("Functions", "public.f_dep.sql", """
            CREATE OR REPLACE FUNCTION public.f_dep() RETURNS bigint LANGUAGE sql
            BEGIN ATOMIC
              SELECT CASE WHEN count(*) > 0 THEN sum(a) ELSE 0 END FROM public.t_dep;
            END;
            """);
        WriteFile("Views", "public.v_dep.sql", "CREATE OR REPLACE VIEW public.v_dep AS SELECT id, a FROM public.t_dep;");
    }

    // PG-090: a materialized view was dropped as "DROP VIEW" (refused: it is not a view), and a policy over the column blocked
    // the change outright. Both are declared, so both are dropped and created again in the same run.
    [Test]
    public void AColumnTypeChange_UnderADeclaredMaterializedViewAndPolicy_Deploys_AndBothAreBack()
    {
        Exec("DROP MATERIALIZED VIEW IF EXISTS public.mv_dep; " + Drops);
        try
        {
            void WriteWith(string type)
            {
                WritePackage("", "Materialized Views");
                WriteFile("Tables", "public.t_dep.json", $$"""
                    { "Schema": "public", "Name": "t_dep",
                      "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
                      "Indexes": [ { "Name": "pk_t_dep", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ],
                      "Policies": [ { "Name": "pol_dep", "Command": "SELECT", "UsingExpression": "a > 0" } ] }
                    """);
                WriteFile("Materialized Views", "public.mv_dep.json", """
                    { "Schema": "public", "Name": "mv_dep", "Definition": "SELECT sum(a) AS total FROM public.t_dep" }
                    """);
            }
            WriteWith("integer");
            Deploy();
            WriteWith("bigint");
            var changed = Deploy();
            Assert.Multiple(() =>
            {
                Assert.That(Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t_dep'::regclass AND attname = 'a'"), Is.EqualTo("bigint"));
                Assert.That(Count("SELECT count(*) FROM pg_matviews WHERE matviewname = 'mv_dep'"), Is.EqualTo(1), "the materialized view is back");
                Assert.That(Count("SELECT count(*) FROM pg_policies WHERE policyname = 'pol_dep'"), Is.EqualTo(1), "and the policy");
                Assert.That(changed.Audit.Select(r => $"{r.ObjectType} {r.Action}"), Is.SupersetOf(new[] { "materializedView dropped", "policy dropped" }), changed.DescribeAudit());
            });
        }
        finally
        {
            Exec("DROP MATERIALIZED VIEW IF EXISTS public.mv_dep; " + Drops);
        }
    }

    // PG-091: a change PostgreSQL will not cast on its own (text to integer) needs USING, which the table JSON cannot say.
    // The documented recipe is a guarded Before script; the table pass then finds the column already at its type.
    [Test]
    public void AColumnTypeChange_PostgreSQLWillNotCast_DeploysThroughTheDocumentedBeforeScript()
    {
        Exec(Drops);
        try
        {
            void WriteWith(string type, bool withScript)
            {
                WritePackage("""{ "FolderPath": "Before", "QuenchSlot": "Before" }""", "Before");
                WriteFile("Tables", "public.t_dep.json", $$"""
                    { "Schema": "public", "Name": "t_dep",
                      "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
                      "Indexes": [ { "Name": "pk_t_dep", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
                    """);
                if (withScript) WriteFile("Before", "t_dep.a to integer.sql", """
                    DO $$
                    BEGIN
                      IF EXISTS (SELECT 1 FROM information_schema.columns
                                 WHERE table_schema = 'public' AND table_name = 't_dep' AND column_name = 'a' AND data_type = 'text') THEN
                        ALTER TABLE public.t_dep ALTER COLUMN a TYPE integer USING trim(a)::integer;
                      END IF;
                    END $$;
                    """);
            }
            WriteWith("text", withScript: false);
            Deploy();
            Exec("INSERT INTO public.t_dep VALUES (1, ' 42 ')");

            WriteWith("integer", withScript: false);
            var refused = Deploy(expectSuccess: false);
            Assert.That(refused.Log, Has.Some.Contains("cannot be cast automatically"), string.Join("\n", refused.Log));

            WriteWith("integer", withScript: true);
            Deploy();
            Assert.Multiple(() =>
            {
                Assert.That(Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t_dep'::regclass AND attname = 'a'"), Is.EqualTo("integer"));
                Assert.That(Scalar("SELECT a FROM public.t_dep WHERE id = 1"), Is.EqualTo(42), "converted by the script's USING");
            });
        }
        finally
        {
            Exec(Drops);
        }
    }

    // A trigger's WHEN clause and a publication's row filter over the column block the change too. The catalog describes
    // both in full, so neither needs declaring: these are made by hand, and both come back as they were.
    [Test]
    public void AColumnTypeChange_UnderATriggerWhenClauseAndAPublicationRowFilter_Deploys_AndBothAreBack()
    {
        var withPublication = Convert.ToInt32(Scalar("SELECT current_setting('server_version_num')::int")) >= 150000;
        const string cleanup = "DROP PUBLICATION IF EXISTS pub_dep; DROP TABLE IF EXISTS public.t_dep; DROP FUNCTION IF EXISTS public.trg_dep_fn()";
        Exec(cleanup);
        try
        {
            void WriteTable(string type)
            {
                WritePackage("", []);
                WriteFile("Tables", "public.t_dep.json", $$"""
                    { "Schema": "public", "Name": "t_dep",
                      "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
                      "Indexes": [ { "Name": "pk_t_dep", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
                    """);
            }
            WriteTable("integer");
            Deploy();
            Exec("""
                CREATE FUNCTION public.trg_dep_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
                CREATE TRIGGER trg_dep BEFORE UPDATE OF a ON public.t_dep FOR EACH ROW WHEN (NEW.a > 0) EXECUTE FUNCTION public.trg_dep_fn();
                ALTER TABLE public.t_dep DISABLE TRIGGER trg_dep;
                COMMENT ON TRIGGER trg_dep ON public.t_dep IS 'kept';
                """);
            if (withPublication) Exec("CREATE PUBLICATION pub_dep FOR TABLE public.t_dep (id, a) WHERE (a > 0)");

            WriteTable("bigint");
            var changed = Deploy();
            Assert.Multiple(() =>
            {
                Assert.That(Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t_dep'::regclass AND attname = 'a'"), Is.EqualTo("bigint"));
                Assert.That(Scalar("SELECT COALESCE((SELECT pg_get_triggerdef(oid) || ' ' || tgenabled::text || ' ' || obj_description(oid, 'pg_trigger') FROM pg_trigger WHERE tgname = 'trg_dep'), 'missing')"),
                    Does.Contain("UPDATE OF a").And.Contain("WHEN ((new.a > 0))").And.EndWith(" D kept"), "the trigger is back, still disabled, with its comment");
                if (withPublication)
                    Assert.That(Scalar("SELECT COALESCE((SELECT array_to_string(attnames, ',') || ' ' || rowfilter FROM pg_publication_tables WHERE pubname = 'pub_dep'), 'missing')"),
                        Is.EqualTo("id,a (a > 0)"), "and the publication has its column list and row filter");
                Assert.That(changed.Audit.Select(r => $"{r.ObjectType} {r.Action}"), Is.SupersetOf(new[] { "trigger dropped", "trigger created" }), changed.DescribeAudit());
            });
        }
        finally
        {
            Exec(cleanup);
        }
    }

    // What the package does not declare cannot be put back, so the deploy stops by name before dropping anything.
    [Test]
    public void AColumnTypeChange_UnderAViewThePackageDoesNotDeclare_StopsBeforeDroppingAnything()
    {
        Exec("DROP VIEW IF EXISTS public.v_mine; " + Drops);
        try
        {
            WritePackage("", []);
            void WriteTable(string type) => WriteFile("Tables", "public.t_dep.json", $$"""
                { "Schema": "public", "Name": "t_dep",
                  "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
                  "Indexes": [ { "Name": "pk_t_dep", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
                """);
            WriteTable("integer");
            Deploy();
            Exec("CREATE VIEW public.v_mine AS SELECT a FROM public.t_dep");
            WriteTable("bigint");
            var refused = Deploy(expectSuccess: false);
            Assert.Multiple(() =>
            {
                Assert.That(refused.Log, Has.Some.Contains("view public.v_mine"), string.Join("\n", refused.Log));
                Assert.That(Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t_dep'::regclass AND attname = 'a'"), Is.EqualTo("integer"));
                Assert.That(Count("SELECT count(*) FROM pg_views WHERE viewname = 'v_mine'"), Is.EqualTo(1), "and the view is still there");
            });
        }
        finally
        {
            Exec("DROP VIEW IF EXISTS public.v_mine; " + Drops);
        }
    }

    [Test]
    public void AColumnTypeChange_UnderADependentViewAndBeginAtomicFunction_Deploys_AndBothAreBackInTheSameRun()
    {
        if (Convert.ToInt32(Scalar("SELECT current_setting('server_version_num')::int")) < 140000)
            Assert.Ignore("SQL-standard routine bodies are PostgreSQL 14.");
        Exec(Drops);
        try
        {
            Write("integer");
            var first = Deploy();
            Assert.That(first.Outcomes.Values, Is.All.EqualTo(ScriptOutcome.Applied), "the BEGIN ATOMIC function deploys: " + first.Describe());
            Exec("INSERT INTO public.t_dep VALUES (1, 20), (2, 22)");

            Write("bigint");
            var changed = Deploy();
            Assert.Multiple(() =>
            {
                Assert.That(Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t_dep'::regclass AND attname = 'a'"), Is.EqualTo("bigint"));
                Assert.That(Scalar("SELECT public.f_dep()"), Is.EqualTo(42), "the function is back in the same run, and works");
                Assert.That(Count("SELECT count(*) FROM public.v_dep"), Is.EqualTo(2), "and so is the view");
                Assert.That(changed.Audit.Select(r => $"{r.ObjectType} {r.ObjectName} {r.Action}"),
                    Is.SupersetOf(new[] { "view public.v_dep dropped", "function public.f_dep dropped" }), changed.DescribeAudit());
            });

            var again = Deploy();
            Assert.That(again.Outcomes.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "then a redeploy runs neither: " + again.Describe());
        }
        finally
        {
            Exec(Drops);
        }
    }
}
