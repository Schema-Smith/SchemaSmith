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
