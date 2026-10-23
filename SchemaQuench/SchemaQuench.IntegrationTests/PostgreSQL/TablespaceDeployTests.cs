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
        Exec("DROP TABLE IF EXISTS public.ts_deploy_a, public.ts_deploy_b; DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'TablespaceDeployProduct'");

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
}
