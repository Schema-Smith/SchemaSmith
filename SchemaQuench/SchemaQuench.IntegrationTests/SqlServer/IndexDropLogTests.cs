// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>The drop line named the index bare, [dbo].[Orders].IX_Orders_Region, where the create line brackets it.</summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class IndexDropLogTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "IndexDropLogProduct";

    private static string TableJson(bool withRegionIndex) => $$"""
        { "Name": "[ixlog_orders]", "Schema": "[dbo]",
          "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false }, { "Name": "[region]", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "[PK_ixlog_orders]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" }
                       {{(withRegionIndex ? """, { "Name": "[IX_ixlog_region]", "IndexColumns": "[region]" }""" : "")}} ] }
        """;

    private void CleanUp() => Exec("DROP TABLE IF EXISTS [dbo].[ixlog_orders]; IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'IndexDropLogProduct'");

    [Test]
    public void TheDropLine_BracketsTheIndexAsTheCreateLineDoes()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "ixlog_orders.json", TableJson(withRegionIndex: true));
            Deploy();

            WriteFile("Tables", "ixlog_orders.json", TableJson(withRegionIndex: false));
            var second = Deploy();

            Assert.That(second.Log.Where(l => l != null && l.Contains("IX_ixlog_region")), Has.Some.Contains("Dropping index [dbo].[ixlog_orders].[IX_ixlog_region]"),
                string.Join("\n", second.Log.Where(l => l != null && l.Contains("IX_ixlog_region"))));
        }
        finally
        {
            CleanUp();
        }
    }
}
