// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Where a small deploy's time goes: kindling a fresh database, the first deploy into it, and the warm redeploys after.
/// <para>
/// The companion to <see cref="DeployPipelineBenchmark"/>, which measures a package too large for compile time to
/// matter. On a small package it is nearly all that matters: the first deploy into a fresh database spends most of its
/// time compiling, and every later call still recompiles the statements that read a temp table its caller re-created.
/// That is the cost a test suite pays per fixture database, and a multi-database deploy pays per database. Each round
/// prints, per phase, wall time and the compile and execution time SQL Server reports (execution sums nested statements,
/// so read it as relative); then recompiles by cause, the cached-plan size for the database, and the compile time by
/// procedure and by statement from Query Store, counting only the compiles after a statement's first.
/// </para>
/// <para>
/// Explicit and with no category, as <see cref="DeployPipelineBenchmark"/> is, so CI never selects it. Run it by name,
/// on a quiet server -- another workload on the same instance inflates every number:
/// <c>dotnet test --filter FullyQualifiedName~FreshDatabaseCostBenchmark</c>
/// </para>
/// </summary>
[TestFixture]
[Explicit("Benchmark: builds and drops databases and an event session, asserts nothing.")]
[NonParallelizable]
public class FreshDatabaseCostBenchmark : BaseTableQuenchTests
{
    private const int Rounds = 3;
    private const string SessionName = "SchemaSmithFreshBench";

    private const string Package = """
[
  { "Schema": "[dbo]", "Name": "[BenchParent]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                 { "Name": "[Code]", "DataType": "VARCHAR(20)", "Nullable": false },
                 { "Name": "[Label]", "DataType": "NVARCHAR(200)", "Nullable": true, "Default": "('x')" },
                 { "Name": "[Qty]", "DataType": "INT", "Nullable": true, "CheckExpression": "[Qty] >= 0" } ],
    "Indexes": [ { "Name": "[PK_BenchParent]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                 { "Name": "[UX_BenchParent_Code]", "Unique": true, "IndexColumns": "[Code]" },
                 { "Name": "[IX_BenchParent_Label]", "IndexColumns": "[Label]", "IncludeColumns": "[Qty]" } ] },
  { "Schema": "[dbo]", "Name": "[BenchChild]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                 { "Name": "[ParentId]", "DataType": "INT", "Nullable": true },
                 { "Name": "[Note]", "DataType": "VARCHAR(50)", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_BenchChild]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                 { "Name": "[IX_BenchChild_Parent]", "IndexColumns": "[ParentId]" } ],
    "ForeignKeys": [ { "Name": "[FK_BenchChild_Parent]", "Columns": "[ParentId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[BenchParent]", "RelatedColumns": "[Id]" } ] }
]
""";

    private static readonly Regex CompileTime = new(@"parse and compile time:\s*CPU time = \d+ ms, elapsed time = (\d+) ms", RegexOptions.IgnoreCase);
    private static readonly Regex ExecutionTime = new(@"Execution Times:\s*CPU time = \d+ ms,\s*elapsed time = (\d+) ms", RegexOptions.IgnoreCase);

    [Test]
    public void MeasureFreshDatabaseAndPerCallCost()
    {
        for (var round = 1; round <= Rounds; round++) OneRound(round);
    }

    private void OneRound(int round)
    {
        var db = $"SchemaSmithFreshBench_{Guid.NewGuid():N}"[..40];
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        long compileMs = 0, execMs = 0;
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
            {
                var c = CompileTime.Match(err.Message);
                if (c.Success) compileMs += long.Parse(c.Groups[1].Value);
                var x = ExecutionTime.Match(err.Message);
                if (x.Success) execMs += long.Parse(x.Groups[1].Value);
            }
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        try
        {
            Execute(cmd, $"CREATE DATABASE [{db}]");
            Execute(cmd, $"ALTER DATABASE [{db}] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL)");
            conn.ChangeDatabase(db);
            var kindle = Stopwatch.StartNew();
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true);
            TestContext.Out.WriteLine($"R{round} kindle {kindle.ElapsedMilliseconds} ms");

            void Quench(string label, string json)
            {
                compileMs = 0;
                execMs = 0;
                Execute(cmd, "SET STATISTICS TIME ON");
                var wall = Stopwatch.StartNew();
                RunTableQuenchProc(cmd, json, productName: "Bench");
                wall.Stop();
                Execute(cmd, "SET STATISTICS TIME OFF");
                TestContext.Out.WriteLine($"R{round} {label,-8} wall {wall.ElapsedMilliseconds,6} ms  compile {compileMs,6} ms  exec {execMs,6} ms");
            }

            Quench("first", Package);
            // Recompiles are counted from the warm calls only: the first call compiles everything anyway.
            Execute(cmd, $@"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = '{SessionName}') DROP EVENT SESSION [{SessionName}] ON SERVER;
CREATE EVENT SESSION [{SessionName}] ON SERVER ADD EVENT sqlserver.sql_statement_recompile(WHERE sqlserver.database_name = N'{db}')
ADD TARGET package0.ring_buffer(SET max_memory = 102400, max_events_limit = 100000) WITH (MAX_DISPATCH_LATENCY = 1 SECONDS);
ALTER EVENT SESSION [{SessionName}] ON SERVER STATE = START;");
            for (var i = 1; i <= 3; i++) Quench($"noop{i}", Package);
            for (var i = 1; i <= 3; i++)
                Quench($"change{i}", Package.Replace("\"VARCHAR(50)\"", i % 2 == 1 ? "\"VARCHAR(60)\"" : "\"VARCHAR(50)\""));

            System.Threading.Thread.Sleep(2000);
            Report(cmd, $@"
SELECT v.cause, COUNT(*)
  FROM (SELECT CAST(t.target_data AS XML) td FROM sys.dm_xe_session_targets t
          JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = '{SessionName}') d
 CROSS APPLY td.nodes('RingBufferTarget/event') n(x)
 CROSS APPLY (SELECT x.value('(data[@name=""recompile_cause""]/text)[1]', 'nvarchar(100)') AS cause) v
 GROUP BY v.cause ORDER BY 2 DESC", r => $"R{round} recompiles {r.GetString(0),-30} {r.GetInt32(1),6}");
            Report(cmd, @"
SELECT CAST(SUM(CAST(cp.size_in_bytes AS BIGINT)) / 1024 AS INT), COUNT(*)
  FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) a
 WHERE a.attribute = 'dbid' AND CONVERT(INT, a.value) = DB_ID()", r => $"R{round} cached plans {r.GetInt32(0)} KB in {r.GetInt32(1)}");
            Report(cmd, @"
SELECT TOP 12 ISNULL(OBJECT_NAME(st.objectid, st.dbid), cp.objtype), CAST(cp.size_in_bytes / 1024 AS INT)
  FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
 WHERE st.dbid = DB_ID() ORDER BY cp.size_in_bytes DESC", r => $"R{round} plan {r.GetString(0),-45} {r.GetInt32(1),8} KB");

            Execute(cmd, "EXEC sp_query_store_flush_db");
            Report(cmd, @"
SELECT TOP 15 ISNULL(OBJECT_NAME(q.object_id), '(ad hoc)'), SUM(q.count_compiles),
       CAST(SUM(q.avg_compile_duration * (q.count_compiles - 1)) / 1000 AS INT)
  FROM sys.query_store_query q GROUP BY OBJECT_NAME(q.object_id) ORDER BY 3 DESC",
                r => $"R{round} recompile ms {r.GetString(0),-45} compiles {r.GetInt64(1),5} ms {r.GetInt32(2),7}");
            Report(cmd, @"
SELECT TOP 15 ISNULL(OBJECT_NAME(q.object_id), '(ad hoc)'), q.count_compiles,
       CAST((q.count_compiles - 1) * q.avg_compile_duration / 1000 AS INT),
       REPLACE(REPLACE(LEFT(t.query_sql_text, 120), CHAR(13), ' '), CHAR(10), ' ')
  FROM sys.query_store_query q JOIN sys.query_store_query_text t ON t.query_text_id = q.query_text_id
 ORDER BY 3 DESC", r => $"R{round} statement {r.GetString(0),-40} n {r.GetInt64(1),4} ms {r.GetInt32(2),6} {r.GetString(3)}");
        }
        finally
        {
            conn.ChangeDatabase("master");
            Execute(cmd, $"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = '{SessionName}') DROP EVENT SESSION [{SessionName}] ON SERVER");
            Execute(cmd, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
        }
    }

    private static void Execute(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Report(IDbCommand cmd, string sql, Func<IDataRecord, string> line)
    {
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) TestContext.Out.WriteLine(line(r));
    }
}
