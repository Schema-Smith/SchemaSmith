// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Every table step reads temp tables its caller has just created. Written one way, SQL Server reuses the step's plans
// on the next deploy; written another, it recompiles the step's statements on every call, which roughly doubled the
// cost of every deploy into a warm database. The suite's database has CDC enabled, which is what reaches the CDC checks.
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
        var session = $"SchemaSmithPlanReuse_{Guid.NewGuid():N}"[..40];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            DropTables(cmd);
            RunTableQuenchProc(cmd, Package);
            RunTableQuenchProc(cmd, Package);

            Execute(cmd, $@"
DECLARE @sql NVARCHAR(MAX) = N'CREATE EVENT SESSION [{session}] ON SERVER
  ADD EVENT sqlserver.sql_statement_recompile (WHERE sqlserver.session_id = ' + CAST(@@SPID AS NVARCHAR(10)) + N')
  ADD TARGET package0.ring_buffer (SET max_memory = 10240) WITH (MAX_DISPATCH_LATENCY = 1 SECONDS)';
EXEC (@sql);
ALTER EVENT SESSION [{session}] ON SERVER STATE = START;");
            RunTableQuenchProc(cmd, Package);
            RunTableQuenchProc(cmd, Package);
            Thread.Sleep(1500);

            var recompiles = TempTableRecompiles(cmd, session);
            Assert.That(recompiles, Is.Empty,
                "an unchanged redeploy recompiled these statements because their caller re-created a temp table: " +
                string.Join("; ", recompiles));
        }
        finally
        {
            Execute(cmd, $"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = '{session}') DROP EVENT SESSION [{session}] ON SERVER");
            DropTables(cmd);
        }
    }

    private static List<string> TempTableRecompiles(IDbCommand cmd, string session)
    {
        cmd.CommandText = $@"
SELECT ISNULL(OBJECT_NAME(v.object_id), '(ad hoc)') + ': ' +
       ISNULL(LTRIM(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(v.object_id), v.offset / 2 + 1, 100), CHAR(13), ' '), CHAR(10), ' ')), '')
  FROM (SELECT CAST(t.target_data AS XML) td FROM sys.dm_xe_session_targets t
          JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = '{session}') d
 CROSS APPLY td.nodes('RingBufferTarget/event') n(x)
 CROSS APPLY (SELECT x.value('(data[@name=""recompile_cause""]/text)[1]', 'NVARCHAR(100)') AS cause,
                     x.value('(data[@name=""object_id""]/value)[1]', 'INT') AS object_id,
                     x.value('(data[@name=""offset""]/value)[1]', 'INT') AS offset) v
 WHERE v.cause = 'Temp table changed'";
        using var reader = cmd.ExecuteReader();
        var found = new List<string>();
        while (reader.Read()) found.Add(reader.GetString(0));
        return found.Distinct().ToList();
    }

    private static void DropTables(IDbCommand cmd)
    {
        Execute(cmd, "DROP TABLE IF EXISTS dbo.PlanReuseChild; DROP TABLE IF EXISTS dbo.PlanReuseParent;");
    }

    private static void Execute(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
