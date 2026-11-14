// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A declared SQL Server sequence through the whole deploy: converged before tables, so a column that defaults to
/// NEXT VALUE FOR it deploys on a fresh database, and an edited increment reaches the values the table is given.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class SequenceDeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "SequenceDeployProduct";

    private const string TableJson = """
        { "Schema": "[dbo]", "Name": "[sq_deploy_orders]",
          "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false, "Default": "NEXT VALUE FOR [dbo].[sq_deploy_ids]" },
                       { "Name": "[note]", "DataType": "NVARCHAR(20)", "Nullable": true } ] }
        """;

    private static string SequenceJson(int increment) =>
        $$"""{ "Schema": "dbo", "Name": "sq_deploy_ids", "DataType": "int", "Start": 1, "Increment": {{increment}} }""";

    private void CleanUp() =>
        Exec("IF OBJECT_ID('dbo.sq_deploy_orders') IS NOT NULL DROP TABLE [dbo].[sq_deploy_orders]; " +
             "IF OBJECT_ID('dbo.sq_deploy_ids', 'SO') IS NOT NULL DROP SEQUENCE [dbo].[sq_deploy_ids]; " +
             "IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'SequenceDeployProduct'");

    [Test]
    public void AColumnDefaultingToADeclaredSequence_Deploys_AndAnEditedIncrementReachesItsValues()
    {
        CleanUp();
        try
        {
            WritePackage("", "Sequences");
            WriteFile("Sequences", "dbo.sq_deploy_ids.json", SequenceJson(1));
            WriteFile("Tables", "dbo.sq_deploy_orders.json", TableJson);
            Deploy();
            Exec("INSERT [dbo].[sq_deploy_orders] ([note]) VALUES (N'first')");

            WriteFile("Sequences", "dbo.sq_deploy_ids.json", SequenceJson(10));
            Deploy();
            Exec("INSERT [dbo].[sq_deploy_orders] ([note]) VALUES (N'second')");

            Assert.Multiple(() =>
            {
                Assert.That(Count("SELECT [id] FROM [dbo].[sq_deploy_orders] WHERE [note] = N'first'"), Is.EqualTo(1));
                Assert.That(Count("SELECT [id] FROM [dbo].[sq_deploy_orders] WHERE [note] = N'second'"), Is.EqualTo(11),
                    "the edited increment landed; the guarded scripted form skipped it once the sequence existed");
            });
        }
        finally
        {
            CleanUp();
        }
    }
}
