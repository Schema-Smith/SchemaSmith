// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A declared tablespace that does not exist is refused before anything is created, on the path a deploy takes. The check
/// lived only in the TableQuench wrapper, which a deploy does not call, so the deploy failed partway with 42704 after
/// creating the tables declared ahead of it.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class TablespaceDeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "TablespaceDeployProduct";

    private void CleanUp() =>
        Exec("DROP TABLE IF EXISTS public.ts_deploy_a, public.ts_deploy_b, public.ts_deploy_p; DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'TablespaceDeployProduct'");

    [Test]
    public void AMissingTablespace_IsRefusedBeforeAnyTableIsCreated()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "public.ts_deploy_a.json",
                """{ "Schema": "public", "Name": "ts_deploy_a", "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ] }""");
            WriteFile("Tables", "public.ts_deploy_b.json",
                """{ "Schema": "public", "Name": "ts_deploy_b", "Tablespace": "ss_no_such_ts", "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ] }""");

            var errors = Deploy(expectSuccess: false).Log.Where(l => l != null && l.StartsWith("ERROR")).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(errors, Has.Some.Contains("Tablespace not found").And.Contains("ss_no_such_ts"), string.Join("\n", errors));
                Assert.That(Count("SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ts_deploy_a', 'ts_deploy_b')"),
                    Is.Zero, "nothing is created before the refusal");
            });
        }
        finally
        {
            CleanUp();
        }
    }

    // A partitioned table refuses the database's default tablespace by name; declared on one, it is still created there.
    [Test]
    public void APartitionedTableDeclaredOnTheDefaultTablespace_IsCreated_AndRedeploysClean()
    {
        CleanUp();
        try
        {
            var defaultTablespace = Scalar("SELECT t.spcname FROM pg_tablespace t JOIN pg_database d ON d.dattablespace = t.oid WHERE d.datname = current_database()")?.ToString();
            WritePackage("");
            WriteFile("Tables", "public.ts_deploy_p.json", $$"""
                { "Schema": "public", "Name": "ts_deploy_p", "Tablespace": "{{defaultTablespace}}",
                  "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "k", "DataType": "integer", "Nullable": false } ],
                  "Partitioning": { "Method": "RANGE", "Expression": "id", "Partitions": [
                      { "Name": "ts_deploy_p1", "Bound": "FOR VALUES FROM (0) TO (100)", "Tablespace": "{{defaultTablespace}}",
                        "Partitioning": { "Method": "LIST", "Expression": "k", "Partitions": [ { "Name": "ts_deploy_p1a", "Bound": "DEFAULT" } ] } } ] } }
                """);

            Deploy();
            var again = Deploy();

            Assert.Multiple(() =>
            {
                Assert.That(Count("SELECT COUNT(*) FROM pg_class WHERE relname IN ('ts_deploy_p', 'ts_deploy_p1', 'ts_deploy_p1a')"), Is.EqualTo(3));
                Assert.That(again.Audit.Where(r => r.Action is "created" or "modified" or "dropped").Select(r => $"{r.ObjectType} {r.ObjectName} {r.Action}"), Is.Empty,
                    "the redeploy changes nothing");
            });
        }
        finally
        {
            CleanUp();
        }
    }
}
