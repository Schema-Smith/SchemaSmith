// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A ledger table deployed once and then failed every redeploy with "Invalid object name '#LedgerColumns'": the deploy
/// runs the lower half of the table ingest through sp_executesql, which dropped the temp table on return, before
/// LedgerQuench read it for the existing table. The engine's ledger tests run the script in one batch and missed it.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class LedgerRedeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "LedgerRedeployProduct";

    private const string TableJson = """
        { "Schema": "[dbo]", "Name": "[ledger_redeploy]", "Ledger": "Updatable",
          "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[V]", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "[PK_ledger_redeploy]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
        """;

    private void CleanUp() =>
        Exec("IF OBJECT_ID('dbo.ledger_redeploy') IS NOT NULL DROP TABLE [dbo].[ledger_redeploy]; " +
             "IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'LedgerRedeployProduct'");

    [Test]
    public void ALedgerTable_RedeploysAsWellAsItDeploys()
    {
        if (Count("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT)") < 16)
            Assert.Ignore("Ledger tables require SQL Server 2022.");
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "ledger_redeploy.json", TableJson);
            Deploy();
            Deploy();

            Assert.That(Count("SELECT ledger_type FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.ledger_redeploy')"), Is.EqualTo(2),
                "still an updatable ledger table after the redeploy");
        }
        finally
        {
            CleanUp();
        }
    }
}
