// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class LockTimeoutDeployTests : LockTimeoutDeploySharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "LockTimeoutProduct";

    protected override string TableFileName => "public.lk_t.json";
    protected override string SmallType => "integer";
    protected override string BigType => "bigint";
    protected override string TableJson(string type) => $$"""
        { "Schema": "public", "Name": "lk_t",
          "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
          "Indexes": [ { "Name": "pk_lk_t", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
        """;
    // ACCESS SHARE is what a plain SELECT holds; it is enough to make ALTER TABLE wait.
    protected override string HoldLock => "LOCK TABLE public.lk_t IN ACCESS SHARE MODE";
    protected override string ColumnType() => Scalar("SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'lk_t' AND column_name = 'a'")?.ToString();
    protected override string DropTable => "DROP TABLE IF EXISTS public.lk_t";

    // An online index build (OnlineApply Prefer: CREATE INDEX CONCURRENTLY) that times out waiting for another session leaves an
    // invalid index. Dropping it waits for the same session, so while that session holds the table it stays: the deploy must say
    // so, report the lock timeout rather than the drop's, and the next deploy must build the index rather than take the
    // half-built one for finished.
    [Test]
    public void AnOnlineIndexBuildThatTimesOut_IsReportedAndFinishedByTheNextDeploy()
    {
        WriteFile("Tables", TableFileName, """
            { "Schema": "public", "Name": "lk_t",
              "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "integer", "Nullable": true } ],
              "Indexes": [ { "Name": "pk_lk_t", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                           { "Name": "ix_lk_t_a", "IndexColumns": "a" } ] }
            """);
        using var blocker = Schema.DataAccess.DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        blocker.Open();
        blocker.ChangeDatabase(MainDb);
        var transaction = blocker.BeginTransaction();
        using (var cmd = blocker.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT * FROM public.lk_t";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { }
        }
        DeployResult result;
        try
        {
            result = Deploy(expectSuccess: false, settings: new System.Collections.Generic.Dictionary<string, string>
            {
                ["OnlineApply"] = "Prefer",
                ["Target:LockTimeoutSeconds"] = "2",
                ["Target:ContentionRetryAttempts"] = "1",
            });
        }
        finally
        {
            transaction.Rollback();
        }

        var invalidAfterFailure = Count("SELECT count(*) FROM pg_index WHERE indrelid = to_regclass('public.lk_t') AND NOT indisvalid");

        // The next deploy, with the lock gone, must not take the half-built index for a finished one.
        Deploy(settings: new System.Collections.Generic.Dictionary<string, string> { ["OnlineApply"] = "Prefer" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Failure?.Kind, Is.EqualTo("lockTimeout"), result.Failure?.Error);
            Assert.That(invalidAfterFailure, Is.EqualTo(1), "premise: the drop could not run while the table was held");
            Assert.That(result.Log.Any(l => l != null && l.Contains("the next deploy builds it again")), Is.True, "the deploy says the invalid index was left");
            Assert.That(Count("SELECT count(*) FROM pg_index WHERE indexrelid = to_regclass('public.ix_lk_t_a') AND indisvalid"), Is.EqualTo(1),
                "the next deploy finishes the index");
        });
    }
}
