// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// OnlineApply on SQL Server: under Prefer an index, constraint or column change on an existing table runs ONLINE where
/// the edition and version allow it, and the plain statement otherwise, with the audit's Mode saying which; Require
/// refuses what cannot run online before anything on the table changes; Off is the plain deploy. The demo server is
/// Developer edition; an edition without online builds is simulated through the CONTEXT_INFO flag the gate reads. An
/// option-only index change (PAD_INDEX, IGNORE_DUP_KEY) is rebuilt in place whatever the setting.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_OnlineApplyTests : BaseTableQuenchTests
{
    private static string Json(string table, string indexes, string columns = "", string tableOnline = null) => $$"""
        [{
            "Schema": "[dbo]",
            "Name": "[{{table}}]",{{(tableOnline == null ? "" : $"\n    \"OnlineApply\": \"{tableOnline}\",")}}
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[V]", "DataType": "VARCHAR(10)", "Nullable": true },
                { "Name": "[W]", "DataType": "INT", "Nullable": true }{{columns}}
            ],
            "Indexes": [ {{indexes}} ]
        }]
        """;

    private static string Pk(string table, string extra = "") =>
        $$"""{ "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"{{extra}} }""";

    private static string Index(string name, string columns, string extra = "") =>
        $$"""{ "Name": "[{{name}}]", "IndexColumns": "{{columns}}"{{extra}} }""";

    private void Quench(IDbCommand cmd, string json, string online = "OFF", int? maxDuration = null, bool indexOnly = false, bool whatIf = false)
    {
        cmd.CommandTimeout = 300;
        var defs = json.Replace("'", "''");
        var duration = maxDuration?.ToString() ?? "NULL";
        cmd.CommandText = indexOnly
            ? $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{_productName}', @TableDefinitions = '{defs}', @DropUnknownIndexes = 1, @WhatIf = {(whatIf ? 1 : 0)}, @OnlineApply = '{online}', @OnlineApplyMaxDuration = {duration}"
            : $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{defs}', @WhatIf = {(whatIf ? 1 : 0)}, @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0, @OnlineApply = '{online}', @OnlineApplyMaxDuration = {duration}";
        var tries = 1;
        while (true)
        {
            try
            {
                TransientRetry.Run(() => cmd.ExecuteNonQuery());
                return;
            }
            catch (SqlException e) when (tries++ < 10 && e.Message.Contains("deadlock victim", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(1000);
            }
        }
    }

    // The deploy's own progress messages and, under WhatIf, the statements it would run.
    private string Printed(IDbConnection conn, IDbCommand cmd, string json, string online, int? maxDuration = null, bool indexOnly = false, bool whatIf = true)
    {
        var captured = new StringBuilder();
        var sqlConn = (SqlConnection)conn;
        SqlInfoMessageEventHandler handler = (_, e) => captured.AppendLine(e.Message);
        sqlConn.InfoMessage += handler;
        try
        {
            Quench(cmd, json, online, maxDuration, indexOnly, whatIf);
        }
        finally
        {
            sqlConn.InfoMessage -= handler;
        }
        return captured.ToString();
    }

    private static List<string> Audits(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT ObjectName + '|' + ActionType + '|' + ISNULL(Mode, '-') FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%' ORDER BY Id";
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private static void SimulateEditionWithoutOnlineBuilds(IDbCommand cmd)
    {
        cmd.CommandText = "DECLARE @c VARBINARY(128) = 0x53534F56 + CONVERT(BINARY(4), CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))) + 0x5353454401; SET CONTEXT_INFO @c";
        cmd.ExecuteNonQuery();
    }

    // A resumable CREATE INDEX is SQL Server 2019 (major 15); 2017 only resumes a rebuild.
    private static bool ResumableCreate(IDbCommand cmd) =>
        Convert.ToInt32(Scalar(cmd, "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))")) >= 15;

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string Keys(IDbCommand cmd, string table, string index) =>
        Scalar(cmd, $"""
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
              FROM sys.indexes i JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id AND ic.key_ordinal > 0
              JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.column_id = ic.column_id
             WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.name = '{index}'
            """) is string keys ? keys : null;

    private void WithTable(string table, Action<IDbConnection, IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            body(conn, cmd);
        }
        finally
        {
            cmd.CommandText = $"SET CONTEXT_INFO 0x; DROP TABLE IF EXISTS dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    [TestCase(false)]
    [TestCase(true)]
    public void Prefer_BuildsANewIndexAndConstraintOnAnExistingTableOnline_AndOffLeavesThemPlain(bool indexOnly)
    {
        var t = Unique("OnPref");
        WithTable(t, (conn, cmd) =>
        {
            Quench(cmd, Json(t, Pk(t)));
            var v2 = Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]") + "," +
                             $$"""{ "Name": "[UQ_{{t}}]", "UniqueConstraint": true, "Unique": true, "IndexColumns": "[W]" }""");

            var off = Printed(conn, cmd, v2, "OFF", indexOnly: indexOnly);
            var prefer = Printed(conn, cmd, v2, "PREFER", indexOnly: indexOnly);
            ClearAudit(cmd);
            Quench(cmd, v2, "PREFER", indexOnly: indexOnly);

            Assert.Multiple(() =>
            {
                Assert.That(off, Does.Contain($"[IX_{t}]"), "the plain deploy creates the index");
                Assert.That(off, Does.Not.Contain("ONLINE = ON"), "Off emits the statements a deploy always has");
                Assert.That(prefer.Split('\n').Any(l => l.Contains($"INDEX [IX_{t}]") && l.Contains("ONLINE = ON")), Is.True, prefer);
                Assert.That(prefer.Split('\n').Any(l => l.Contains($"ADD CONSTRAINT [UQ_{t}]") && l.Contains("ONLINE = ON")), Is.True, prefer);
                Assert.That(Audits(cmd, t), Is.SupersetOf(new[] { $"[dbo].[{t}].[IX_{t}]|created|online", $"[dbo].[{t}].[UQ_{t}]|created|online" }));
                Assert.That(Keys(cmd, t, $"IX_{t}"), Is.EqualTo("V"));
                Assert.That(Keys(cmd, t, $"UQ_{t}"), Is.EqualTo("W"));
            });
        });
    }

    [Test]
    public void Prefer_BuildsTheIndexesOfATableThisDeployCreates_Plain()
    {
        var t = Unique("OnNew");
        WithTable(t, (_, cmd) =>
        {
            ClearAudit(cmd);
            Quench(cmd, Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]")), "PREFER");
            Assert.That(Audits(cmd, t).Where(a => a.Contains("|created|") && a.Contains($"].[{t}].")), Is.EquivalentTo(new[] { $"[dbo].[{t}].[PK_{t}]|created|-", $"[dbo].[{t}].[IX_{t}]|created|-" }),
                "a new table is empty, so its indexes are built the plain way");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prefer_WhereTheEditionCannotBuildOnline_BuildsPlain_AndRecordsWhy(bool indexOnly)
    {
        var t = Unique("OnEd");
        WithTable(t, (conn, cmd) =>
        {
            Quench(cmd, Json(t, Pk(t)));
            SimulateEditionWithoutOnlineBuilds(cmd);
            var v2 = Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]"));
            var printed = Printed(conn, cmd, v2, "PREFER", indexOnly: indexOnly);
            ClearAudit(cmd);
            Quench(cmd, v2, "PREFER", indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(printed, Does.Not.Contain("ONLINE = ON"));
                Assert.That(printed, Does.Contain("(offline: online index operations need Enterprise or Developer edition"));
                Assert.That(Audits(cmd, t), Does.Contain($"[dbo].[{t}].[IX_{t}]|created|unavailable"));
                Assert.That(Keys(cmd, t, $"IX_{t}"), Is.EqualTo("V"));
            });
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Require_WhereTheEditionCannotBuildOnline_IsRefusedBeforeAnythingChanges(bool indexOnly)
    {
        var t = Unique("OnReqEd");
        WithTable(t, (_, cmd) =>
        {
            Quench(cmd, Json(t, Pk(t)));
            SimulateEditionWithoutOnlineBuilds(cmd);
            var ex = Assert.Catch(() => Quench(cmd, Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]"),
                                                          tableOnline: "Require"), indexOnly: indexOnly));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"OnlineApply is Require for [dbo].[{t}]").And.Contain("Enterprise or Developer edition"));
                Assert.That(Keys(cmd, t, $"IX_{t}"), Is.Null, "nothing was built");
            });
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Require_RefusesAColumnstoreIndexByName_BeforeDroppingTheIndexItWouldBuildAgain(bool indexOnly)
    {
        var t = Unique("OnReqCs");
        WithTable(t, (_, cmd) =>
        {
            Quench(cmd, Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]")));
            var v2 = Json(t, Pk(t) + "," + Index($"IX_{t}", "[V],[W]") + "," +
                             $$"""{ "Name": "[CS_{{t}}]", "ColumnStore": true, "IncludeColumns": "[W]" }""");
            var ex = Assert.Catch(() => Quench(cmd, v2, "REQUIRE", indexOnly: indexOnly));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("cannot be built online").And.Contain($"[CS_{t}]").And.Contain("a columnstore index is built offline"));
                Assert.That(ex.Message, Does.Not.Contain($"[IX_{t}]"), "the changed rowstore index can be built online");
                Assert.That(Keys(cmd, t, $"IX_{t}"), Is.EqualTo("V"), "refused before the changed index was dropped");
                Assert.That(Keys(cmd, t, $"CS_{t}"), Is.Null);
            });

            ClearAudit(cmd);
            Quench(cmd, v2, "PREFER", indexOnly: indexOnly);
            Assert.That(Audits(cmd, t), Is.SupersetOf(new[] { $"[dbo].[{t}].[CS_{t}]|created|unavailable", $"[dbo].[{t}].[IX_{t}]|created|online" }),
                "Prefer builds the columnstore index the plain way and says so");
        });
    }

    // An index that cannot be built online and already exists is refused when its change would drop it to build it again.
    [TestCase(false)]
    [TestCase(true)]
    public void Require_RefusesAChangedColumnstoreIndex_BeforeItIsDropped(bool indexOnly)
    {
        var t = Unique("OnReqCs2");
        WithTable(t, (_, cmd) =>
        {
            string Version(string columns) => Json(t, Pk(t) + "," + $$"""{ "Name": "[CS_{{t}}]", "ColumnStore": true, "IncludeColumns": "{{columns}}" }""");
            Quench(cmd, Version("[W]"));
            var ex = Assert.Catch(() => Quench(cmd, Version("[V],[W]"), "REQUIRE", indexOnly: indexOnly));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("cannot be built online").And.Contain($"[CS_{t}]"));
                Assert.That(Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id WHERE i.[object_id] = OBJECT_ID('dbo.{t}') AND i.name = 'CS_{t}'")),
                    Is.EqualTo(1), "the columnstore index is still the one deployed");
            });
        });
    }

    [Test]
    public void Prefer_AltersAColumnOnline_AndRequireRefusesOneThatCannotBe_BeforeTheTableChanges()
    {
        var t = Unique("OnCol");
        WithTable(t, (conn, cmd) =>
        {
            Quench(cmd, Json(t, Pk(t)));
            var widened = Json(t, Pk(t)).Replace("\"VARCHAR(10)\"", "\"VARCHAR(20)\"");
            var printed = Printed(conn, cmd, widened, "PREFER");
            ClearAudit(cmd);
            Quench(cmd, widened, "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(printed.Split('\n').Any(l => l.Contains("ALTER COLUMN [V] VARCHAR(20)") && l.Contains("WITH (ONLINE = ON)")), Is.True, printed);
                Assert.That(Audits(cmd, t), Does.Contain($"[dbo].[{t}].[V]|modified|online"));
                Assert.That(GetColumnDataType(cmd, t, "V"), Is.EqualTo("VARCHAR(20)"));
            });

            // A columnstore index on the table rules an online column change out.
            var withColumnStore = Json(t, Pk(t) + "," + $$"""{ "Name": "[CS_{{t}}]", "ColumnStore": true, "IncludeColumns": "[W]" }""")
                .Replace("\"VARCHAR(10)\"", "\"VARCHAR(20)\"");
            Quench(cmd, withColumnStore);
            var wider = withColumnStore.Replace("\"VARCHAR(20)\"", "\"VARCHAR(30)\"");
            var ex = Assert.Catch(() => Quench(cmd, wider, "REQUIRE"));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("these column changes cannot run online").And.Contain($"[dbo].[{t}].[V] (the table has a columnstore index)"));
                Assert.That(GetColumnDataType(cmd, t, "V"), Is.EqualTo("VARCHAR(20)"), "refused before the column changed");
            });

            ClearAudit(cmd);
            Quench(cmd, wider, "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Does.Contain($"[dbo].[{t}].[V]|modified|unavailable"));
                Assert.That(GetColumnDataType(cmd, t, "V"), Is.EqualTo("VARCHAR(30)"));
            });
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnIndexWhoseOnlyChangeIsAnOption_IsRebuiltInPlace_NotDroppedAndCreated(bool indexOnly)
    {
        var t = Unique("OnOpt");
        WithTable(t, (conn, cmd) =>
        {
            string Version(bool on) => Json(t,
                Pk(t, on ? ", \"PadIndex\": true" : "") + "," +
                Index($"IX_{t}", "[V]", on ? ", \"PadIndex\": true" : "") + "," +
                Index($"UX_{t}", "[W]", ", \"Unique\": true" + (on ? ", \"IgnoreDuplicateKey\": true" : "")));
            Quench(cmd, Version(false));
            cmd.CommandText = $"INSERT dbo.{t} (Id, W) VALUES (1, 1), (2, 2)";
            cmd.ExecuteNonQuery();

            foreach (var on in new[] { true, false })
            {
                ClearAudit(cmd);
                Quench(cmd, Version(on), indexOnly: indexOnly);
                var flag = on ? 1 : 0;
                Assert.Multiple(() =>
                {
                    Assert.That(Audits(cmd, t), Is.EquivalentTo(new[]
                    {
                        $"[dbo].[{t}].[PK_{t}]|modified|-", $"[dbo].[{t}].[IX_{t}]|modified|-", $"[dbo].[{t}].[UX_{t}]|modified|-"
                    }), $"rebuilt in place, nothing dropped (PadIndex/IgnoreDuplicateKey {on})");
                    Assert.That(Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{t}') AND name IN ('PK_{t}', 'IX_{t}') AND is_padded = {flag}")), Is.EqualTo(2));
                    Assert.That(Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{t}') AND name = 'UX_{t}' AND ignore_dup_key = {flag}")), Is.EqualTo(1));
                });
                ClearAudit(cmd);
                Quench(cmd, Version(on), indexOnly: indexOnly);
                Assert.That(Audits(cmd, t), Is.Empty, "a redeploy changes nothing");
            }

            var printed = Printed(conn, cmd, Version(true), "PREFER", indexOnly: indexOnly);
            ClearAudit(cmd);
            Quench(cmd, Version(true), "PREFER", indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(printed, Does.Contain($"ALTER INDEX [IX_{t}] ON [dbo].[{t}] REBUILD WITH (PAD_INDEX = ON, IGNORE_DUP_KEY = OFF, ONLINE = ON)"));
                Assert.That(Audits(cmd, t), Does.Contain($"[dbo].[{t}].[IX_{t}]|modified|online"), "the rebuild runs online under Prefer");
            });
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prefer_RebuildsACompressionOrFillFactorChangeOnline(bool indexOnly)
    {
        var t = Unique("OnReb");
        WithTable(t, (conn, cmd) =>
        {
            string Version(string compression, int fill) =>
                Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]", $", \"CompressionType\": \"{compression}\", \"FillFactor\": {fill}"))
                    .Replace($"\"Name\": \"[{t}]\",", $"\"Name\": \"[{t}]\", \"UpdateFillFactor\": true,");
            Quench(cmd, Version("NONE", 80));
            var printed = Printed(conn, cmd, Version("PAGE", 90), "PREFER", indexOnly: indexOnly);
            Quench(cmd, Version("PAGE", 90), "PREFER", indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(printed, Does.Contain($"ALTER INDEX [IX_{t}] ON [dbo].[{t}] REBUILD PARTITION=ALL WITH (DATA_COMPRESSION=PAGE, ONLINE = ON)"));
                Assert.That(printed, Does.Contain($"ALTER INDEX [IX_{t}] ON [dbo].[{t}] REBUILD WITH (FILLFACTOR = 90, SORT_IN_TEMPDB = ON, ONLINE = ON)"));
                Assert.That(Scalar(cmd, $"SELECT CONVERT(VARCHAR(10), i.fill_factor) + ':' + p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.[object_id] = i.[object_id] AND p.index_id = i.index_id WHERE i.[object_id] = OBJECT_ID('dbo.{t}') AND i.name = 'IX_{t}'"),
                    Is.EqualTo("90:PAGE"));
            });
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AConstraintsIgnoreDupKeyChange_IsStillDroppedAndCreated(bool indexOnly)
    {
        var t = Unique("OnUqIdk");
        WithTable(t, (_, cmd) =>
        {
            string Version(bool on) => Json(t, Pk(t) + "," +
                $$"""{ "Name": "[UQ_{{t}}]", "UniqueConstraint": true, "Unique": true, "IndexColumns": "[W]"{{(on ? ", \"IgnoreDuplicateKey\": true" : "")}} }""");
            Quench(cmd, Version(false));
            ClearAudit(cmd);
            Quench(cmd, Version(true), indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.SupersetOf(new[] { $"[dbo].[{t}].UQ_{t}|dropped|-", $"[dbo].[{t}].[UQ_{t}]|created|-" }),
                    "SQL Server cannot change a constraint's IGNORE_DUP_KEY in place (1979)");
                Assert.That(Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{t}') AND name = 'UQ_{t}' AND ignore_dup_key = 1 AND is_unique_constraint = 1")), Is.EqualTo(1));
            });
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prefer_DropsAChangedClusteredIndexOnline_AndBuildsItAgainOnline(bool indexOnly)
    {
        var t = Unique("OnCx");
        WithTable(t, (_, cmd) =>
        {
            string Version(string keys) => Json(t, $$"""{ "Name": "[CX_{{t}}]", "Clustered": true, "IndexColumns": "{{keys}}" }""");
            Quench(cmd, Version("[V]"));
            ClearAudit(cmd);
            Quench(cmd, Version("[V],[W]"), "PREFER", indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.SupersetOf(new[] { $"[dbo].[{t}].CX_{t}|dropped|online", $"[dbo].[{t}].[CX_{t}]|created|online" }));
                Assert.That(Keys(cmd, t, $"CX_{t}"), Is.EqualTo("V,W"));
            });
        });
    }

    [Test]
    public void WithAMaxDuration_AnIndexIsBuiltResumably_ExceptOverAComputedKey()
    {
        var t = Unique("OnRes");
        WithTable(t, (conn, cmd) =>
        {
            const string computed = """, { "Name": "[C]", "DataType": "INT", "ComputedExpression": "[W] * 2", "Persisted": true }""";
            Quench(cmd, Json(t, Pk(t), computed));
            var v2 = Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]") + "," + Index($"IXC_{t}", "[C]"), computed);
            var printed = Printed(conn, cmd, v2, "PREFER", maxDuration: 5);
            ClearAudit(cmd);
            Quench(cmd, v2, "PREFER", maxDuration: 5);
            var resumable = ResumableCreate(cmd);
            Assert.Multiple(() =>
            {
                Assert.That(printed.Split('\n').Any(l => l.Contains($"INDEX [IX_{t}]") && (resumable
                    ? l.Contains("ONLINE = ON, RESUMABLE = ON, MAX_DURATION = 5 MINUTES")
                    : l.Contains("ONLINE = ON") && !l.Contains("RESUMABLE"))), Is.True, printed);
                Assert.That(printed.Split('\n').Any(l => l.Contains($"INDEX [IXC_{t}]") && l.Contains("ONLINE = ON") && !l.Contains("RESUMABLE")), Is.True,
                    "SQL Server refuses a resumable build over a computed key (10644)");
                Assert.That(Audits(cmd, t), Is.SupersetOf(new[] { $"[dbo].[{t}].[IX_{t}]|created|{(resumable ? "resumable" : "online")}", $"[dbo].[{t}].[IXC_{t}]|created|online" }));
            });
        });
    }

    // A build an earlier deploy left paused (its MAX_DURATION ran out) blocks every other DDL on the table (10637). The
    // next deploy, whatever its settings, resumes the one it still declares and abandons the one it does not, before it
    // makes the column change it brings. The builds are paused with ALTER INDEX ... PAUSE from another session, which
    // leaves the same state an elapsed MAX_DURATION does.
    [Test]
    [NonParallelizable]
    public void APausedBuild_IsResumedWhenStillDeclared_AndAbandonedWhenNot_BeforeTheTableChanges()
    {
        // One paused build per table: while one is paused the engine refuses even a second CREATE INDEX there (10637).
        var kept = Unique("OnPause");
        var gone = Unique("OnPauseGone");
        WithTable(gone, (_, _) => WithTable(kept, (_, cmd) =>
        {
            if (!ResumableCreate(cmd)) Assert.Ignore("A resumable CREATE INDEX needs SQL Server 2019 (major 15).");
            string Both(string keptIndexes) => "[" + Json(kept, Pk(kept) + keptIndexes).Replace("\"VARCHAR(10)\"", "\"VARCHAR(20)\"").Trim().TrimStart('[').TrimEnd(']') +
                                                "," + Json(gone, Pk(gone)).Replace("\"VARCHAR(10)\"", "\"VARCHAR(20)\"").Trim().TrimStart('[').TrimEnd(']') + "]";
            Quench(cmd, Json(kept, Pk(kept)));
            Quench(cmd, Json(gone, Pk(gone)));
            cmd.CommandTimeout = 600;
            foreach (var t in new[] { kept, gone })
            {
                cmd.CommandText = $"""
                    WITH n AS (SELECT TOP (1000000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) r FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                    INSERT dbo.{t} (Id, V, W) SELECT r, 'v', CHECKSUM(NEWID()) FROM n
                    """;
                cmd.ExecuteNonQuery();
            }
            PauseABuild(kept, $"IX_{kept}");
            PauseABuild(gone, $"IX_{gone}");

            ClearAudit(cmd);
            Quench(cmd, Both("," + Index($"IX_{kept}", "[W]")));
            Assert.Multiple(() =>
            {
                Assert.That(Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.index_resumable_operations WHERE [object_id] IN (OBJECT_ID('dbo.{kept}'), OBJECT_ID('dbo.{gone}'))")), Is.Zero);
                Assert.That(Keys(cmd, kept, $"IX_{kept}"), Is.EqualTo("W"), "the declared build was resumed");
                Assert.That(Keys(cmd, gone, $"IX_{gone}"), Is.Null, "the undeclared one was abandoned");
                Assert.That(GetColumnDataType(cmd, kept, "V"), Is.EqualTo("VARCHAR(20)"), "the column changes ran once the tables were free");
                Assert.That(GetColumnDataType(cmd, gone, "V"), Is.EqualTo("VARCHAR(20)"));
                Assert.That(Audits(cmd, kept), Does.Contain($"[dbo].[{kept}].[IX_{kept}]|created|resumable"));
            });
        }));
    }

    private void PauseABuild(string table, string index)
    {
        var build = Task.Run(() =>
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = $"CREATE INDEX [{index}] ON dbo.{table} ([W]) WITH (ONLINE = ON, RESUMABLE = ON, MAXDOP = 1)";
            try { cmd.ExecuteNonQuery(); } catch (SqlException) { }
        });
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !build.IsCompleted)
        {
            if (Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.index_resumable_operations WHERE [object_id] = OBJECT_ID('dbo.{table}') AND name = '{index}' AND state = 0")) == 1)
            {
                cmd.CommandText = $"ALTER INDEX [{index}] ON dbo.{table} PAUSE";
                cmd.ExecuteNonQuery();
                break;
            }
            Thread.Sleep(50);
        }
        build.Wait(TimeSpan.FromSeconds(120));
        Assert.That(Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM sys.index_resumable_operations WHERE [object_id] = OBJECT_ID('dbo.{table}') AND name = '{index}' AND state = 1")), Is.EqualTo(1),
            $"{index} was paused before it finished; grow the table if the build now outruns the poll");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheIndexesADeployCreatesAndDrops_AreRecordedForTheDeploymentSummary(bool indexOnly)
    {
        var t = Unique("OnAud");
        WithTable(t, (_, cmd) =>
        {
            Quench(cmd, Json(t, Pk(t)));
            ClearAudit(cmd);
            Quench(cmd, Json(t, Pk(t) + "," + Index($"IX_{t}", "[V]")), indexOnly: indexOnly);
            Assert.That(Audits(cmd, t), Is.EquivalentTo(new[] { $"[dbo].[{t}].[IX_{t}]|created|-" }));
            ClearAudit(cmd);
            Quench(cmd, Json(t, Pk(t) + "," + Index($"IX_{t}", "[W]")), indexOnly: indexOnly);
            Assert.That(Audits(cmd, t), Is.EquivalentTo(new[] { $"[dbo].[{t}].IX_{t}|dropped|-", $"[dbo].[{t}].[IX_{t}]|created|-" }));
        });
    }
}
