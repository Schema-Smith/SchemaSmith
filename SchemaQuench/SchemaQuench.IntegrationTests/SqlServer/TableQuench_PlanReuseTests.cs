// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
        cmd.CommandText = "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 15) Assert.Ignore("Before SQL Server 2019, a statement reading its caller's temp table recompiles on every call.");
        try
        {
            DropTables(cmd);
            RunTableQuenchProc(cmd, Package);
            RunTableQuenchProc(cmd, Package);

            // The defect recompiles the same statements on every call, so one redeploy without them settles it. A statement
            // reading a temp table also recompiles when SQL Server has not kept that temp table cached, which a loaded server
            // can repeat for a few calls; measuring until the repeated set empties keeps that from reading as the defect.
            List<string> everyTime = null;
            for (var i = 0; i < 6 && everyTime is not { Count: 0 }; i++)
            {
                using var capture = RecompileCapture.ForThisSession(cmd);
                RunTableQuenchProc(cmd, Package);
                var recompiled = capture.TempTableRecompiles();
                TestContext.Out.WriteLine($"redeploy {i + 1} recompiled: {string.Join("; ", recompiled)}");
                everyTime = everyTime == null ? recompiled : everyTime.Intersect(recompiled).ToList();
            }

            Assert.That(everyTime, Is.Empty,
                "every unchanged redeploy recompiled these statements because their caller re-created a temp table: " +
                string.Join("; ", everyTime));
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
