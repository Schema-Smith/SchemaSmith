// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Counts the statements SQL Server recompiled because a temp table changed, through an Extended Events session scoped
/// to one session or one database. Before reading, it runs a statement certain to recompile and waits for that event,
/// so an empty answer means nothing recompiled rather than that the capture had not delivered yet.
/// </summary>
internal sealed class RecompileCapture : IDisposable
{
    private readonly IDbCommand _cmd;
    private readonly string _name = $"SchemaSmithRecompile_{Guid.NewGuid():N}"[..40];

    private RecompileCapture(IDbCommand cmd, string predicate)
    {
        _cmd = cmd;
        Execute($@"
DECLARE @sql NVARCHAR(MAX) = N'CREATE EVENT SESSION [{_name}] ON SERVER
  ADD EVENT sqlserver.sql_statement_recompile (ACTION (sqlserver.database_name) WHERE ' + {predicate} + N')
  ADD TARGET package0.ring_buffer (SET max_memory = 10240) WITH (MAX_DISPATCH_LATENCY = 1 SECONDS)';
EXEC (@sql);
ALTER EVENT SESSION [{_name}] ON SERVER STATE = START;");
    }

    /// <summary>Captures what the command's own session recompiles.</summary>
    public static RecompileCapture ForThisSession(IDbCommand cmd) =>
        new(cmd, "N'sqlserver.session_id = ' + CAST(@@SPID AS NVARCHAR(10))");

    /// <summary>Captures what any session recompiles in the command's current database.</summary>
    public static RecompileCapture ForThisDatabase(IDbCommand cmd) =>
        new(cmd, "N'sqlserver.database_name = N''' + REPLACE(DB_NAME(), '''', '''''') + N''''");

    /// <summary>The statements recompiled because a temp table changed, each named by its procedure and text.</summary>
    public List<string> TempTableRecompiles()
    {
        Execute("DECLARE @n INT; SELECT @n = COUNT(*) FROM sys.objects OPTION (RECOMPILE)");
        var clock = Stopwatch.StartNew();
        while (Events().All(e => e.Cause != "Option (recompile) requested"))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                Assert.Fail("the recompile capture never delivered its control event, so it cannot say nothing recompiled");
            Thread.Sleep(250);
        }
        return Events().Where(e => e.Cause == "Temp table changed").Select(e => e.Statement).Distinct().ToList();
    }

    private List<(string Cause, string Statement)> Events()
    {
        _cmd.CommandText = $@"
SELECT v.cause, ISNULL(OBJECT_NAME(v.object_id), '(ad hoc)') + ': ' +
       ISNULL(LTRIM(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(v.object_id), v.offset / 2 + 1, 100), CHAR(13), ' '), CHAR(10), ' ')), '')
  FROM (SELECT CAST(t.target_data AS XML) td FROM sys.dm_xe_session_targets t
          JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = '{_name}') d
 CROSS APPLY td.nodes('RingBufferTarget/event') n(x)
 CROSS APPLY (SELECT x.value('(data[@name=""recompile_cause""]/text)[1]', 'NVARCHAR(100)') AS cause,
                     x.value('(data[@name=""object_id""]/value)[1]', 'INT') AS object_id,
                     x.value('(data[@name=""offset""]/value)[1]', 'INT') AS offset) v";
        var events = new List<(string, string)>();
        using var reader = _cmd.ExecuteReader();
        while (reader.Read()) events.Add((reader.IsDBNull(0) ? "" : reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return events;
    }

    private void Execute(string sql)
    {
        _cmd.CommandText = sql;
        _cmd.ExecuteNonQuery();
    }

    public void Dispose() =>
        Execute($"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = '{_name}') DROP EVENT SESSION [{_name}] ON SERVER");
}
