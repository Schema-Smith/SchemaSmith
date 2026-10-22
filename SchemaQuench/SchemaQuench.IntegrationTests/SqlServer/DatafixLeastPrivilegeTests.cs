// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A datafix deploy (UpdateTables false) as the documented least-privilege login. The run recorded the expression map
/// and read the change audit regardless, so the login's missing rights on SchemaSmith's own objects were logged as
/// errors on every run although the fix applied.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class DatafixLeastPrivilegeTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "DatafixLeastPrivilege";

    private const string Login = "ss_datafix_lp";
    private readonly string _password = "Df!" + Guid.NewGuid().ToString("N")[..20];

    private void DropAll()
    {
        // The deploys leave the login's pooled connections open, and a connected login cannot be dropped.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Exec("DROP TABLE IF EXISTS [dbo].[df_price]");
        Exec($"IF SCHEMA_ID('df_lp') IS NOT NULL DROP SCHEMA [df_lp]");
        Exec($"IF USER_ID('{Login}') IS NOT NULL DROP USER [{Login}]");
        Exec($"IF SUSER_ID('{Login}') IS NOT NULL DROP LOGIN [{Login}]");
        Exec("DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'DatafixLeastPrivilege'");
    }

    [SetUp]
    public void SetUp() => DropAll();

    [TearDown]
    public void TearDown() => DropAll();

    private void ArrangeTheDatafix(bool withTrackingGrants)
    {
        WritePackage("""{ "FolderPath": "Before", "QuenchSlot": "Before" }""", "Before");
        WriteFile("Tables", "df_price.json", """
            { "Name": "[df_price]", "Schema": "[dbo]",
              "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false }, { "Name": "[price]", "DataType": "INT", "Nullable": false } ],
              "Indexes": [ { "Name": "[PK_df_price]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
            """);
        Deploy();
        Exec("INSERT INTO dbo.df_price VALUES (1, 10)");
        Exec($"""
            CREATE LOGIN [{Login}] WITH PASSWORD = '{_password}', CHECK_POLICY = OFF;
            CREATE USER [{Login}] FOR LOGIN [{Login}];
            EXEC('CREATE SCHEMA [df_lp] AUTHORIZATION [{Login}]');
            GRANT SELECT, INSERT, UPDATE ON SCHEMA::dbo TO [{Login}];
            GRANT CREATE TABLE TO [{Login}];
            GRANT EXECUTE ON SCHEMA::dbo TO [{Login}];
            """);
        if (withTrackingGrants)
            Exec($"GRANT SELECT, INSERT, DELETE ON SchemaSmith.CompletedMigrationScripts TO [{Login}]");
        WriteFile("Before", "fix-price.sql", "UPDATE dbo.df_price SET price = price + 1 WHERE id = 1");
    }

    [Test]
    public void ADatafixDeploy_AsTheDocumentedAccount_AppliesTheFixOnce_AndLogsNoBookkeepingErrors()
    {
        ArrangeTheDatafix(withTrackingGrants: true);

        var result = Deploy(updateTables: false, login: (Login, _password));
        var again = Deploy(updateTables: false, login: (Login, _password));

        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT price FROM dbo.df_price WHERE id = 1"), Is.EqualTo(11), "the fix applied, once: the second deploy found it recorded");
            Assert.That(result.Log.Concat(again.Log).Where(IsBookkeepingError), Is.Empty, "a run that updates no tables does not touch SchemaSmith's other tables");
        });
    }

    // Without rights on the run-once record the read failed silently, so every deploy ran the fix again.
    [Test]
    public void ADatafixDeploy_ThatCannotReadTheRunOnceRecord_StopsBeforeRunningTheFix()
    {
        ArrangeTheDatafix(withTrackingGrants: false);

        var result = Deploy(updateTables: false, login: (Login, _password), expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT price FROM dbo.df_price WHERE id = 1"), Is.EqualTo(10), "the fix did not run, so it cannot run twice");
            Assert.That(result.Log.Any(l => l != null && l.Contains("CompletedMigrationScripts") && l.Contains("would run again")), Is.True,
                "the stop names the table and why; log: " + string.Join(" | ", result.Log.Where(l => l != null && l.StartsWith("ERROR"))));
        });
    }

    private static bool IsBookkeepingError(string l) => l != null && (l.Contains("Could not record") || l.Contains("permission was denied") || l.Contains("denied on"));
}
