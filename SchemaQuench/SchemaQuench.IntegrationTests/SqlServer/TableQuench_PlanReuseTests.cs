// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Every table step reads temp tables its caller has just created. Written one way, SQL Server reuses the step's plans
// on the next deploy; written another, it recompiles the step's statements on every call, which roughly doubled the
// cost of every deploy into a warm database. The suite's database has CDC enabled, which is what reaches the CDC checks.
// DeployPathTableFeatureTests asserts the same through a real deploy.
[TestFixture]
[NonParallelizable]
public class TableQuench_PlanReuseTests : BaseTableQuenchTests
{
    private const string Package = """
[
  { "Schema": "[dbo]", "Name": "[PlanReuseParent]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                 { "Name": "[Code]", "DataType": "VARCHAR(20)", "Nullable": false },
                 { "Name": "[Qty]", "DataType": "INT", "Nullable": true, "Default": "((0))", "CheckExpression": "[Qty] >= 0" } ],
    "Indexes": [ { "Name": "[PK_PlanReuseParent]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                 { "Name": "[IX_PlanReuseParent_Code]", "IndexColumns": "[Code]" } ] },
  { "Schema": "[dbo]", "Name": "[PlanReuseChild]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                 { "Name": "[ParentId]", "DataType": "INT", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_PlanReuseChild]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
    "ForeignKeys": [ { "Name": "[FK_PlanReuseChild_Parent]", "Columns": "[ParentId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[PlanReuseParent]", "RelatedColumns": "[Id]" } ] }
]
""";

    [Test]
    public void AnUnchangedRedeploy_ReusesItsPlans_RatherThanRecompilingOnTheCallersTempTables()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            DropTables(cmd);
            RunTableQuenchProc(cmd, Package);
            RunTableQuenchProc(cmd, Package);

            using var capture = RecompileCapture.ForThisSession(cmd);
            RunTableQuenchProc(cmd, Package);
            RunTableQuenchProc(cmd, Package);

            var recompiles = capture.TempTableRecompiles();
            Assert.That(recompiles, Is.Empty,
                "an unchanged redeploy recompiled these statements because their caller re-created a temp table: " +
                string.Join("; ", recompiles));
        }
        finally
        {
            DropTables(cmd);
        }
    }

    private static void DropTables(IDbCommand cmd)
    {
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.PlanReuseChild; DROP TABLE IF EXISTS dbo.PlanReuseParent;";
        cmd.ExecuteNonQuery();
    }
}
