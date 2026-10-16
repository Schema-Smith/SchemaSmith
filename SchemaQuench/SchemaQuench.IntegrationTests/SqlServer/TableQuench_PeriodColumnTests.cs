// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Microsoft.Data.SqlClient;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A temporal table's period columns are declared by their real names: <c>GeneratedAlways: RowStart / RowEnd</c> with
/// the author's name, <c>DATETIME2(n)</c> and <c>Hidden</c>. Declared, they are created as written, extracted as
/// written, renamed and re-hidden in place on an existing table; a precision change is reported, because SQL Server
/// cannot alter a period column. Undeclared, a new table gets the <c>ValidFrom</c>/<c>ValidTo</c> convention and an
/// existing table's pair is left alone, so an <c>IsTemporal</c>-only package behaves exactly as before.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_PeriodColumnTests : BaseTableQuenchTests
{
    private const string Hidden2 = """
        ,{ "Name": "[SysStart]", "DataType": "DATETIME2(2)", "Nullable": false, "GeneratedAlways": "RowStart", "Hidden": true },
         { "Name": "[SysEnd]", "DataType": "DATETIME2(2)", "Nullable": false, "GeneratedAlways": "RowEnd", "Hidden": true }
        """;

    private const string Hidden7 = """
        ,{ "Name": "[SysStart]", "DataType": "DATETIME2(7)", "Nullable": false, "GeneratedAlways": "RowStart", "Hidden": true },
         { "Name": "[SysEnd]", "DataType": "DATETIME2(7)", "Nullable": false, "GeneratedAlways": "RowEnd", "Hidden": true }
        """;

    private static string Json(string table, string periodColumns, string extraIndex = "") => $$"""
        [{
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "IsTemporal": true,
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[N]", "DataType": "INT", "Nullable": true }{{periodColumns}}
            ],
            "Indexes": [
                { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }{{extraIndex}}
            ]
        }]
        """;

    private static string PeriodShape(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT STRING_AGG(name + ':' + CAST(generated_always_type AS VARCHAR) + ':' + CAST(is_hidden AS VARCHAR) + ':' + CAST(scale AS VARCHAR), ',') WITHIN GROUP (ORDER BY generated_always_type)
              FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND generated_always_type IN (1, 2)
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static int TemporalType(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int AuditRows(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private static SqlServerTable Extract(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var json = "";
        while (reader.Read()) json += reader.GetString(0);
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private readonly List<string> _messages = [];

    private void WithTables(Action<IDbCommand> body, params string[] tables)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        if (conn is SqlConnection sql) sql.InfoMessage += (_, e) => { lock (_messages) _messages.Add(e.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            body(cmd);
        }
        finally
        {
            foreach (var t in tables)
            {
                cmd.CommandText = $"""
                    IF OBJECTPROPERTY(OBJECT_ID('dbo.{t}'), 'TableTemporalType') = 2 ALTER TABLE dbo.{t} SET (SYSTEM_VERSIONING = OFF);
                    DROP TABLE IF EXISTS dbo.{t}; DROP TABLE IF EXISTS dbo.{t}_Hist;
                    """;
                cmd.ExecuteNonQuery();
            }
        }
    }

    [Test]
    public void DeclaredPeriodColumns_AreCreatedAsWritten_RedeployAsANoOp_AndRoundTripThroughExtraction()
    {
        const string table = "PeriodDeclared";
        const string copy = "PeriodDeclaredCopy";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, Hidden2, ",{ \"Name\": \"[IX_PeriodDeclared_SysEnd]\", \"IndexColumns\": \"[SysEnd]\" }"));
            Assert.Multiple(() =>
            {
                Assert.That(PeriodShape(cmd, table), Is.EqualTo("SysStart:1:1:2,SysEnd:2:1:2"), "the declared names, hidden, precision 2");
                Assert.That(TemporalType(cmd, table), Is.EqualTo(2), "system-versioned");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND name = 'IX_{table}_SysEnd'"), Is.EqualTo("1"),
                    "an index on a declared period column of a new table is created");
            });
            cmd.CommandText = $"INSERT dbo.{table} (Id, N) VALUES (1, 1); UPDATE dbo.{table} SET N = 2;";
            cmd.ExecuteNonQuery();
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM dbo.{table}_Hist"), Is.EqualTo("1"), "history is written");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, Hidden2, ",{ \"Name\": \"[IX_PeriodDeclared_SysEnd]\", \"IndexColumns\": \"[SysEnd]\" }"));
            Assert.That(AuditRows(cmd, table), Is.Zero, "the redeploy changed nothing");

            var extracted = Extract(cmd, table);
            var periods = extracted.Columns.Cast<SqlServerColumn>().Where(c => c.GeneratedAlways != null).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(periods.Select(c => $"{c.Name}|{c.GeneratedAlways}|{c.DataType}|{c.Hidden}"),
                    Is.EquivalentTo(new[] { "[SysStart]|RowStart|DATETIME2(2)|True", "[SysEnd]|RowEnd|DATETIME2(2)|True" }), "extracted as declared");
                Assert.That(extracted.IsTemporal, Is.True);
            });

            // Back to source: the extracted columns deploy a second table with the same shape.
            var copyJson = Json(copy, "," + string.Join(",", periods.Select(c =>
                $"{{ \"Name\": \"{c.Name}\", \"DataType\": \"{c.DataType}\", \"Nullable\": false, \"GeneratedAlways\": \"{c.GeneratedAlways}\", \"Hidden\": {c.Hidden.ToString().ToLowerInvariant()} }}")));
            RunTableQuenchProc(cmd, copyJson);
            Assert.That(PeriodShape(cmd, copy), Is.EqualTo(PeriodShape(cmd, table)), "the extracted package re-creates the same period columns");
        }, table, copy);
    }

    [Test]
    public void AnIsTemporalOnlyPackage_KeepsTheConvention_AndExtractsNoPeriodColumns()
    {
        const string table = "PeriodConvention";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, ""));
            Assert.That(PeriodShape(cmd, table), Is.EqualTo("ValidFrom:1:0:7,ValidTo:2:0:7"), "today's convention, unchanged");
            Assert.That(Extract(cmd, table).Columns.Select(c => c.Name), Is.EqualTo(new[] { "[Id]", "[N]" }), "nothing new in the extracted package");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, ""));
            Assert.That(AuditRows(cmd, table), Is.Zero, "and a redeploy is a no-op");
        }, table);
    }

    [Test]
    public void AnExistingConventionPair_DeclaredWithNewNamesAndHidden_IsRenamedAndHiddenInPlace_AndAPrecisionChangeIsReported()
    {
        const string table = "PeriodConverge";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, ""));
            cmd.CommandText = $"INSERT dbo.{table} (Id, N) VALUES (1, 1); UPDATE dbo.{table} SET N = 2;";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(table, Hidden7));
            Assert.Multiple(() =>
            {
                Assert.That(PeriodShape(cmd, table), Is.EqualTo("SysStart:1:1:7,SysEnd:2:1:7"), "renamed and hidden, in place");
                Assert.That(TemporalType(cmd, table), Is.EqualTo(2), "still versioned");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM dbo.{table}_Hist"), Is.EqualTo("1"), "history kept");
                Assert.That(Scalar(cmd, $"SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY column_id) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}_Hist')"),
                    Is.EqualTo("Id,N,SysStart,SysEnd"), "the history table follows the rename");
            });

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, Hidden7));
            Assert.That(AuditRows(cmd, table), Is.Zero, "converged: the next deploy is a no-op");

            // A precision change cannot be applied to a period column; the deploy says so and leaves the column.
            lock (_messages) _messages.Clear();
            RunTableQuenchProc(cmd, Json(table, Hidden2));
            string[] warnings;
            lock (_messages) warnings = _messages.Where(m => m.Contains("WARNING") && m.Contains(table)).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(PeriodShape(cmd, table), Is.EqualTo("SysStart:1:1:7,SysEnd:2:1:7"), "precision unchanged");
                Assert.That(warnings, Has.Length.EqualTo(1), "one warning for the table: " + string.Join(" | ", warnings));
                Assert.That(warnings.FirstOrDefault(), Does.Contain("SysStart").And.Contain("DATETIME2(7)").And.Contain("DATETIME2(2)"), "naming the column and both precisions");
            });
        }, table);
    }

    [Test]
    public void AnExistingCustomPair_IsLeftAlone_ByAPackageThatDoesNotDeclareIt()
    {
        const string table = "PeriodUnmanaged";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, Hidden2));
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, ""));
            Assert.Multiple(() =>
            {
                Assert.That(PeriodShape(cmd, table), Is.EqualTo("SysStart:1:1:2,SysEnd:2:1:2"), "an undeclared pair is unmanaged on an existing table");
                Assert.That(AuditRows(cmd, table), Is.Zero);
            });
        }, table);
    }
}
