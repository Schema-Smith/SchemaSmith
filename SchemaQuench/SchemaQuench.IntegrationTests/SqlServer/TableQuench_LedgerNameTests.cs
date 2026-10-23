// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A ledger table's generated columns and its ledger view are declared by their real names. They were not read, so a
/// custom name came back as the engine's default and the next deploy built something else. Declared, they are created
/// as written and extracted only where they differ from the engine's defaults; on an existing table a column is renamed
/// and its HIDDEN set in place, and the view renamed, while what SQL Server cannot change (the view's columns) is
/// reported and the deploy goes on.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_LedgerNameTests : BaseTableQuenchTests
{
    private static string Updatable(string table, string columns = "", string ledgerView = "") => $$"""
        [{ "Schema": "[dbo]", "Name": "[{{table}}]", "Ledger": "Updatable"{{ledgerView}},
           "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[V]", "DataType": "INT", "Nullable": true }{{columns}} ],
           "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }]
        """;

    private const string CustomColumns = """
        , { "Name": "[TxStart]", "DataType": "BIGINT", "Nullable": false, "GeneratedAlways": "TransactionIdStart", "Hidden": true }
        , { "Name": "[TxEnd]", "DataType": "BIGINT", "Nullable": true, "GeneratedAlways": "TransactionIdEnd", "Hidden": true }
        , { "Name": "[SeqStart]", "DataType": "BIGINT", "Nullable": false, "GeneratedAlways": "SequenceNumberStart" }
        """;

    private static string View(string name, string extra = "") => $$""", "LedgerView": { "Name": "[{{name}}]", "TransactionIdColumn": "[TxId]"{{extra}} }""";

    // name:type:hidden for each generated column, in role order, then the view and its four column names.
    private static string Shape(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT ISNULL((SELECT STRING_AGG(CAST(c.[name] + ':' + CAST(c.generated_always_type AS VARCHAR(2)) + ':' + CAST(c.is_hidden AS VARCHAR(1)) AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY c.generated_always_type)
                             FROM sys.columns c WHERE c.[object_id] = t.[object_id] AND c.generated_always_type BETWEEN 7 AND 10), '') + ' | ' +
                   OBJECT_NAME(t.ledger_view_id) + ':' +
                   (SELECT STRING_AGG(CAST(vc.[name] AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY vc.ledger_view_column_type)
                      FROM sys.columns vc WHERE vc.[object_id] = t.ledger_view_id AND vc.ledger_view_column_type <> 0)
              FROM sys.tables t WHERE t.[object_id] = OBJECT_ID('dbo.{table}')
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static int Audits(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static SqlServerTable Extract(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = TransientRetry.Run(() => cmd.ExecuteReader());
        var json = "";
        while (reader.Read()) json += reader.GetString(0);
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
    }

    private void WithTables(Action<IDbCommand, List<string>> body, params string[] tables)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var messages = new List<string>();
        ((SqlConnection)conn).InfoMessage += (_, e) => messages.Add(e.Message);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 16) Assert.Ignore("Ledger tables need SQL Server 2022 (major 16).");
        try { body(cmd, messages); }
        finally
        {
            foreach (var t in tables)
            {
                // DROP on a ledger table renames it out of the way (MSSQL_DroppedLedgerTable_...), which frees the name.
                cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{t}";
                cmd.ExecuteNonQuery();
            }
        }
    }

    [Test]
    public void DeclaredLedgerNames_AreCreatedAsWritten_RedeployAsANoOp_AndRoundTripThroughExtraction()
    {
        const string table = "LedgerNames";
        const string copy = "LedgerNamesCopy";
        WithTables((cmd, _) =>
        {
            RunTableQuenchProc(cmd, Updatable(table, CustomColumns, View("LedgerNamesView")));
            const string expected = "TxStart:7:1,TxEnd:8:1,SeqStart:9:0,ledger_end_sequence_number:10:1 | LedgerNamesView:TxId,ledger_sequence_number,ledger_operation_type,ledger_operation_type_desc";
            Assert.That(Shape(cmd, table), Is.EqualTo(expected), "declared names, the rest the engine's");

            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Updatable(table, CustomColumns, View("LedgerNamesView")));
            Assert.That(Audits(cmd, table), Is.Zero, "a redeploy touches nothing");

            var extracted = Extract(cmd, table);
            var generated = extracted.Columns.OfType<SqlServerColumn>().Where(c => c.GeneratedAlways != null).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(generated.Select(c => $"{c.Name}:{c.GeneratedAlways}:{c.Hidden}"),
                    Is.EquivalentTo(new[] { "[TxStart]:TransactionIdStart:True", "[TxEnd]:TransactionIdEnd:True", "[SeqStart]:SequenceNumberStart:False" }),
                    "only the columns that are not the engine's own");
                Assert.That($"{extracted.LedgerView?.Schema}|{extracted.LedgerView?.Name}|{extracted.LedgerView?.TransactionIdColumn}|{extracted.LedgerView?.SequenceNumberColumn}",
                    Is.EqualTo("|[LedgerNamesView]|[TxId]|"), "and only the view members that differ");
            });

            // Back to source, under another view name so it does not collide with the original's.
            extracted.LedgerView!.Name = "[LedgerNamesCopyView]";
            var copyJson = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { extracted }).Replace($"[{table}]", $"[{copy}]").Replace($"[PK_{table}]", $"[PK_{copy}]");
            RunTableQuenchProc(cmd, copyJson);
            Assert.That(Shape(cmd, copy), Is.EqualTo(expected.Replace("LedgerNamesView", "LedgerNamesCopyView")), "the extracted package builds the same table");
        }, table, copy);
    }

    [Test]
    public void OnAnExistingLedgerTable_ColumnNamesHiddenAndTheViewNameConverge_AndTheViewsColumnNamesAreReported()
    {
        const string table = "LedgerConverge";
        WithTables((cmd, messages) =>
        {
            RunTableQuenchProc(cmd, Updatable(table));
            Assert.That(Shape(cmd, table), Does.StartWith("ledger_start_transaction_id:7:1,"), "precondition: the engine's names");

            RunTableQuenchProc(cmd, Updatable(table, CustomColumns, """, "LedgerView": { "Name": "[LedgerConvergeView]" }"""));
            Assert.That(Shape(cmd, table), Is.EqualTo("TxStart:7:1,TxEnd:8:1,SeqStart:9:0,ledger_end_sequence_number:10:1 | LedgerConvergeView:ledger_transaction_id,ledger_sequence_number,ledger_operation_type,ledger_operation_type_desc"),
                "renamed, SeqStart made visible, the view renamed");

            messages.Clear();
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Updatable(table, CustomColumns, View("LedgerConvergeView"))), "a name SQL Server cannot change does not fail the deploy");
            Assert.Multiple(() =>
            {
                Assert.That(messages.Count(m => m.Contains("WARNING") && m.Contains(table) && m.Contains("TransactionIdColumn ledger_transaction_id (declared TxId)")), Is.EqualTo(1),
                    "it is reported, naming the member");
                Assert.That(Shape(cmd, table), Does.EndWith("LedgerConvergeView:ledger_transaction_id,ledger_sequence_number,ledger_operation_type,ledger_operation_type_desc"));
            });
        }, table);
    }

    [Test]
    public void AnAppendOnlyTable_TakesItsDeclaredStartColumn()
    {
        const string table = "LedgerAppend";
        WithTables((cmd, _) =>
        {
            RunTableQuenchProc(cmd, $$"""
                [{ "Schema": "[dbo]", "Name": "[{{table}}]", "Ledger": "AppendOnly",
                   "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                                { "Name": "[Tx]", "DataType": "BIGINT", "Nullable": false, "GeneratedAlways": "TransactionIdStart" } ] }]
                """);
            Assert.That(Shape(cmd, table), Does.StartWith("Tx:7:0,ledger_start_sequence_number:9:1 | LedgerAppend_Ledger:"));
            var extracted = Extract(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(extracted.Columns.OfType<SqlServerColumn>().Where(c => c.GeneratedAlways != null).Select(c => c.Name), Is.EqualTo(new[] { "[Tx]" }));
                Assert.That(extracted.LedgerView, Is.Null, "a default view is not written");
            });
        }, table);
    }
}
