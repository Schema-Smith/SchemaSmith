// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    /// <summary>
    /// A repeatable measurement of where a deploy's time goes, at a package size no fixture reaches.
    /// <para>
    /// Explicit: it builds a database, deploys thousands of tables into it and drops it again, which is
    /// minutes of work and has no assertion worth gating a build on. It exists because performance work
    /// on this pipeline kept being done from inspection, and inspection was wrong twice -- the column
    /// decision looked like three catalog calls and was really an unindexed LOB join, and a procedure
    /// that looked like the second-largest cost turned out to be a stale copy of itself.
    /// </para>
    /// <para>
    /// It creates and kindles its OWN database rather than measuring against a shared scratch one. That
    /// is the whole point: a partially-kindled database silently measures whatever helpers happen to be
    /// installed, which is how a procedure from an older build got profiled and reported.
    /// </para>
    /// <para>
    /// Run it with:
    /// <c>dotnet test --filter FullyQualifiedName~DeployPipelineBenchmark</c>
    /// </para>
    /// </summary>
    [TestFixture]
    [Category("SqlServer")]
    [Explicit("Benchmark: builds and drops a database, takes minutes, asserts nothing.")]
    [NonParallelizable]
    public class DeployPipelineBenchmark : BaseTableQuenchTests
    {
        // Shaped after a real large package rather than round numbers: wide-ish tables, a couple of
        // indexes each, and enough of them that per-row costs separate from fixed ones.
        private const int TableCount = 1783;
        private const int ColumnsPerTable = 19;
        private const int NoOpRuns = 3;

        private string _benchDb;

        [Test]
        public void MeasureWhereTheDeploySpendsItsTime()
        {
            _benchDb = $"SchemaSmithBench_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var model = BuildModel();
            TestContext.Out.WriteLine($"Model: {TableCount} tables, {TableCount * ColumnsPerTable} columns, {model.Length / 1024 / 1024} MB of JSON");

            using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;

            try
            {
                cmd.CommandText = $"CREATE DATABASE [{_benchDb}]";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(_benchDb);

                var kindle = Time(() => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true));
                // Prove the helper set is complete before trusting a single timing taken against it.
                cmd.CommandText = "SELECT COUNT(*) FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = 'SchemaSmith'";
                var helpers = Convert.ToInt32(cmd.ExecuteScalar());
                TestContext.Out.WriteLine($"Kindle: {kindle} ms, {helpers} SchemaSmith objects");
                foreach (var required in new[] { "ModifiedTableQuench", "MissingTableAndColumnQuench",
                                                 "MissingIndexesAndConstraintsQuench", "ValidateDeclaredTableAttributes" })
                {
                    cmd.CommandText = $"SELECT OBJECT_ID('SchemaSmith.{required}', 'P')";
                    Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value),
                        $"SchemaSmith.{required} is not installed; any timing taken here would measure the wrong code.");
                }

                // COLD COMPILE. Plans cache per object PER DATABASE, so a fresh database never reuses
                // one -- and a CI run creates ~30 of them, each compiling this procedure once and using
                // the plan once. That makes compile time, not execution time, the lever on the slowest
                // leg, so it is measured against a database that has never run the procedure.
                // The working set has to exist for the procedure to compile against it, but it does not
                // have to have rows: compiling is what is being measured, and the plan is built from the
                // declared shapes, not the data.
                cmd.Parameters.Clear();
                cmd.CommandText = ForgeKindler.GetParseTableJsonPhases(Platform.SqlServer).CreateTables;
                cmd.ExecuteNonQuery();
                var coldMs = Time(() =>
                {
                    cmd.CommandText = "EXEC SchemaSmith.ModifiedTableQuench @ProductName = 'Cold', @WhatIf = 1, @DropUnknownIndexes = 0, @DropTablesRemovedFromProduct = 0";
                    cmd.ExecuteNonQuery();
                });
                cmd.CommandText = "SELECT ISNULL(SUM(cp.size_in_bytes)/1024, 0) FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st WHERE OBJECT_NAME(st.objectid, st.dbid) = 'ModifiedTableQuench' AND st.dbid = DB_ID()";
                TestContext.Out.WriteLine($"Cold compile (empty database): {coldMs:N0} ms, cached plan {Convert.ToInt32(cmd.ExecuteScalar()):N0} KB");

                TestContext.Out.WriteLine("\n--- FIRST DEPLOY (everything is new) ---");
                Report(RunPipeline(conn, cmd, model));

                // The no-op pass is repeated and reported as a median. A single run of it was read as
                // noise once when it was really a 2x regression, and believing a one-shot number is how
                // that happened: the first pass leaves the buffer pool and plan cache in a state the
                // next run inherits, so run-to-run spread is real and has to be measured, not assumed.
                var runs = new List<List<(string Step, long Ms)>>();
                for (var i = 0; i < NoOpRuns; i++) runs.Add(RunPipeline(conn, cmd, model));

                TestContext.Out.WriteLine($"\n--- NO-OP RE-DEPLOY (nothing has changed), median of {NoOpRuns} ---");
                ReportMedian(runs);
            }
            finally
            {
                conn.ChangeDatabase("master");
                cmd.CommandText = $"IF DB_ID('{_benchDb}') IS NOT NULL BEGIN ALTER DATABASE [{_benchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_benchDb}]; END";
                cmd.ExecuteNonQuery();
            }
        }

        private static List<(string Step, long Ms)> RunPipeline(SqlConnection conn, SqlCommand cmd, string model)
        {
            var steps = new List<(string, long)>();
            var (createTables, fillTables) = ForgeKindler.GetParseTableJsonPhases(Platform.SqlServer);

            steps.Add(("parse", Time(() =>
            {
                cmd.Parameters.Clear();
                cmd.CommandText = createTables;
                cmd.ExecuteNonQuery();

                cmd.CommandText = "DECLARE @v_SQL NVARCHAR(MAX) = ''\nSET NOCOUNT ON\n" + fillTables;
                cmd.Parameters.Add("@TableDefinitions", SqlDbType.VarChar, -1).Value = model;
                cmd.Parameters.Add("@UpdateFillFactor", SqlDbType.Bit).Value = false;
                cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();
            })));

            foreach (var (step, sql) in new[]
            {
                ("MissingTableAndColumnQuench", "EXEC SchemaSmith.MissingTableAndColumnQuench @WhatIf = 0"),
                ("ModifiedTableQuench", "EXEC SchemaSmith.ModifiedTableQuench @ProductName = 'Benchmark', @WhatIf = 0, @DropUnknownIndexes = 0, @DropTablesRemovedFromProduct = 0"),
                ("MissingIndexesAndConstraintsQuench", "EXEC SchemaSmith.MissingIndexesAndConstraintsQuench 'Benchmark', 0"),
                ("FileStreamColumnQuench", "EXEC SchemaSmith.FileStreamColumnQuench @WhatIf = 0"),
                ("ForeignKeyQuench", "EXEC SchemaSmith.ForeignKeyQuench @ProductName = 'Benchmark', @WhatIf = 0")
            })
            {
                steps.Add((step, Time(() => { cmd.CommandText = sql; cmd.ExecuteNonQuery(); })));
            }

            return steps;
        }

        /// <summary>
        /// Breaks the dominant procedure down by its own progress messages, on a no-op re-deploy.
        /// <para>
        /// The copy it profiles is built from <c>sys.sql_modules</c>, i.e. the definition actually
        /// installed, so the kindle-time tokens are already resolved and there is no second, hand-made
        /// substitution to get wrong.
        /// </para>
        /// </summary>
        [Test]
        public void ProfileTheDominantProcedure()
        {
            _benchDb = $"SchemaSmithProf_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var model = BuildModel();

            using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;

            try
            {
                cmd.CommandText = $"CREATE DATABASE [{_benchDb}]";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(_benchDb);
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true);

                RunPipeline(conn, cmd, model);   // first deploy: everything gets created
                InstallTimedCopy(cmd);
                RunPipeline(conn, cmd, model);   // re-parse so the working set is populated again

                cmd.CommandText = "DROP TABLE IF EXISTS ##prof; CREATE TABLE ##prof([Seq] INT IDENTITY, [Label] NVARCHAR(200), [At] DATETIME2(7))";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "EXEC SchemaSmith.ZZProfiledModifiedTableQuench @ProductName = 'Benchmark', @WhatIf = 0, @DropUnknownIndexes = 0, @DropTablesRemovedFromProduct = 0";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
SELECT [Label], [ms] FROM (
  SELECT [Label], [ms] = DATEDIFF(MILLISECOND, [At], LEAD([At]) OVER (ORDER BY [Seq])) FROM ##prof) z
 WHERE [ms] > 0 ORDER BY [ms] DESC";
                using var reader = cmd.ExecuteReader();
                TestContext.Out.WriteLine("\n--- ModifiedTableQuench, no-op re-deploy, by step ---");
                while (reader.Read())
                    TestContext.Out.WriteLine($"  {reader.GetInt32(1),8:N0} ms  {reader.GetString(0)}");
            }
            finally
            {
                conn.ChangeDatabase("master");
                cmd.CommandText = $"IF DB_ID('{_benchDb}') IS NOT NULL BEGIN ALTER DATABASE [{_benchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_benchDb}]; END";
                cmd.ExecuteNonQuery();
            }
        }

        private static void InstallTimedCopy(SqlCommand cmd)
        {
            cmd.CommandText = "SELECT definition FROM sys.sql_modules WHERE [object_id] = OBJECT_ID('SchemaSmith.ModifiedTableQuench')";
            var body = (string)cmd.ExecuteScalar();

            body = body.Replace("CREATE PROCEDURE SchemaSmith.ModifiedTableQuench",
                                "CREATE PROCEDURE SchemaSmith.ZZProfiledModifiedTableQuench");

            // Whole-line matches only. The procedure BUILDS dynamic SQL containing RAISERROR(...) inside
            // string literals, and rewriting those corrupts the statements it generates.
            var n = 0;
            body = System.Text.RegularExpressions.Regex.Replace(body,
                // The trailing \r? matters: sys.sql_modules hands the definition back with CRLF endings,
                // and a "$" anchor will not step over the carriage return on its own.
                @"(?m)^([ \t]*)RAISERROR\('([^']*)', 10, 100\) WITH NOWAIT[ \t]*\r?$",
                m => $"{m.Groups[1].Value}INSERT INTO ##prof([Label],[At]) VALUES('{++n:D2} {m.Groups[2].Value.Replace("'", "''")}', SYSDATETIME());\n{m.Value}");

            cmd.CommandText = body;
            cmd.ExecuteNonQuery();
            TestContext.Out.WriteLine($"Profiled copy installed with {n} step boundaries");
        }

        private static void Report(List<(string Step, long Ms)> steps)
        {
            var total = steps.Sum(s => s.Ms);
            foreach (var (step, ms) in steps.OrderByDescending(s => s.Ms))
                TestContext.Out.WriteLine($"  {ms,8:N0} ms  {100.0 * ms / Math.Max(total, 1),5:F1}%  {step}");
            TestContext.Out.WriteLine($"  {total,8:N0} ms          TOTAL");
        }

        /// <summary>
        /// Median per step, with the observed spread alongside it. The spread is printed because it is
        /// the thing that says whether a difference between two runs means anything: a step whose own
        /// min and max straddle the change being judged has not measured that change.
        /// </summary>
        private static void ReportMedian(List<List<(string Step, long Ms)>> runs)
        {
            var perStep = runs[0].Select(s => s.Step)
                .Select(step => (Step: step, Times: runs.Select(r => r.First(x => x.Step == step).Ms).OrderBy(m => m).ToList()))
                .Select(x => (x.Step, Median: x.Times[x.Times.Count / 2], Min: x.Times.First(), Max: x.Times.Last()))
                .OrderByDescending(x => x.Median)
                .ToList();

            var total = perStep.Sum(x => x.Median);
            foreach (var (step, median, min, max) in perStep)
                TestContext.Out.WriteLine($"  {median,8:N0} ms  {100.0 * median / Math.Max(total, 1),5:F1}%  {step}  (min {min:N0}, max {max:N0})");
            TestContext.Out.WriteLine($"  {total,8:N0} ms          TOTAL (median)");
        }

        private static long Time(Action action)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        private static string BuildModel()
        {
            var types = new[] { "INT", "VARCHAR(50)", "NVARCHAR(200)", "DATETIME2", "BIT", "DECIMAL(18,4)" };
            var sb = new StringBuilder("[");
            for (var t = 0; t < TableCount; t++)
            {
                if (t > 0) sb.Append(',');
                sb.Append($"{{\"Schema\":\"dbo\",\"Name\":\"B{t:D5}\",\"Columns\":[{{\"Name\":\"Id\",\"DataType\":\"INT\",\"Nullable\":false}}");
                for (var c = 0; c < ColumnsPerTable; c++)
                    sb.Append($",{{\"Name\":\"Col{c:D3}\",\"DataType\":\"{types[(t + c) % types.Length]}\",\"Nullable\":{((t + c) % 2 == 0 ? "true" : "false")}}}");
                sb.Append($"],\"Indexes\":[{{\"Name\":\"PK_B{t:D5}\",\"PrimaryKey\":true,\"Unique\":true,\"Clustered\":true,\"IndexColumns\":\"Id\"}}");
                sb.Append($",{{\"Name\":\"IX_B{t:D5}_Col000\",\"IndexColumns\":\"Col000\"}}]}}");
            }
            return sb.Append(']').ToString();
        }
    }
}
