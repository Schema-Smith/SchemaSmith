// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A datafix deploy (UpdateTables false) as the documented least-privilege role. The run recorded the expression map and
/// read the change audit regardless, so the role's missing rights on the SchemaSmith schema were logged as an error on
/// every run (42501) although the fix applied.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class DatafixLeastPrivilegeTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "DatafixLeastPrivilege";

    private const string Role = "ss_datafix_lp";
    // A fresh password for each test: PostgreSQL drops a role that still has a session, and a pooled connection of the
    // dropped role, keyed by the same name and password, came back to the next test without its rights.
    private string _password = NewPassword();

    private static string NewPassword() => "Df" + Guid.NewGuid().ToString("N")[..20];

    private void DropAll()
    {
        // The deploys leave the login's pooled connections open, and a connected login cannot be dropped.
        Npgsql.NpgsqlConnection.ClearAllPools();
        Exec("DROP TABLE IF EXISTS public.df_price");
        Exec("DROP SCHEMA IF EXISTS df_lp CASCADE");
        Exec($"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Role}') THEN EXECUTE 'DROP OWNED BY {Role}'; EXECUTE 'DROP ROLE {Role}'; END IF; END $$");
        Exec("DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'DatafixLeastPrivilege'");
    }

    [SetUp]
    public void SetUp()
    {
        DropAll();
        _password = NewPassword();
    }

    [TearDown]
    public void TearDown() => DropAll();

    private void ArrangeTheDatafix(bool withTrackingGrants, string trackingGrants = "SELECT, INSERT, DELETE")
    {
        WritePackage("""{ "FolderPath": "Before", "QuenchSlot": "Before" }""", "Before");
        WriteFile("Tables", "public.df_price.json", """
            { "Schema": "public", "Name": "df_price",
              "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "price", "DataType": "integer", "Nullable": false } ],
              "Indexes": [ { "Name": "pk_df_price", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
            """);
        Deploy();
        Exec("INSERT INTO public.df_price VALUES (1, 10)");
        Exec($"""
            CREATE ROLE {Role} LOGIN PASSWORD '{_password}';
            GRANT CONNECT ON DATABASE "{MainDb}" TO {Role};
            GRANT TEMPORARY ON DATABASE "{MainDb}" TO {Role};
            GRANT USAGE ON SCHEMA public TO {Role};
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {Role};
            CREATE SCHEMA df_lp AUTHORIZATION {Role};
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {Role};
            GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA public TO {Role};
            """);
        if (withTrackingGrants)
            Exec($"GRANT USAGE ON SCHEMA \"SchemaSmith\" TO {Role}; GRANT {trackingGrants} ON \"SchemaSmith\".\"CompletedMigrationScripts\" TO {Role}");
        WriteFile("Before", "fix-price.sql", "UPDATE public.df_price SET price = price + 1 WHERE id = 1;");
    }

    [Test]
    public void ADatafixDeploy_AsTheDocumentedAccount_AppliesTheFixOnce_AndLogsNoBookkeepingErrors()
    {
        ArrangeTheDatafix(withTrackingGrants: true);

        var result = Deploy(updateTables: false, login: (Role, _password));
        var again = Deploy(updateTables: false, login: (Role, _password));

        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT price FROM public.df_price WHERE id = 1"), Is.EqualTo(11), "the fix applied, once: the second deploy found it recorded");
            Assert.That(result.Log.Concat(again.Log).Where(IsBookkeepingError), Is.Empty, "a run that updates no tables does not touch SchemaSmith's other tables");
        });
    }

    // Without rights on the run-once record the read failed silently, so every deploy ran the fix again.
    [Test]
    public void ADatafixDeploy_ThatCannotReadTheRunOnceRecord_StopsBeforeRunningTheFix()
    {
        ArrangeTheDatafix(withTrackingGrants: false);

        var result = Deploy(updateTables: false, login: (Role, _password), expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT price FROM public.df_price WHERE id = 1"), Is.EqualTo(10), "the fix did not run, so it cannot run twice");
            Assert.That(result.Log.Any(l => l != null && l.Split('\n').Any(line => line.Contains("Cannot read which") && line.Contains("would run again") && line.Contains("Grant the deploy login"))), Is.True,
                "the stop and the grant arrive as one line, not split by the engine error; log: " + string.Join(" | ", result.Log.Where(l => l != null && l.StartsWith("ERROR"))));
        });
    }

    // The record half: a login that can read the run-once record but not write to it applied the fix and lost the
    // record, so the next deploy ran it again. The script now fails, naming why.
    [Test]
    public void ADatafixDeploy_ThatCannotRecordTheScript_FailsTheScript_AndSaysItWouldRunAgain()
    {
        ArrangeTheDatafix(withTrackingGrants: true, trackingGrants: "SELECT");

        var result = Deploy(updateTables: false, login: (Role, _password), expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Log.Any(l => l != null && l.Contains("could not be recorded as run") && l.Contains("run it again")), Is.True,
                "the failure says the script ran and why it will run again; log: " + string.Join(" | ", result.Log.Where(l => l != null && l.StartsWith("ERROR"))));
            Assert.That(Count("SELECT price FROM " + "public.df_price" + " WHERE id = 1"), Is.EqualTo(11), "it applied once in this run");
        });
    }

    private static bool IsBookkeepingError(string l) => l != null && (l.Contains("Could not record") || l.Contains("42501") || l.Contains("permission denied"));
}
