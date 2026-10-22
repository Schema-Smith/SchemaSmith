// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A disabled index is declared state, not noise. Declared <c>Disabled</c> it is created and then disabled; declared
/// without it (the default) over an index someone disabled by hand it is rebuilt, which is what enables it, so a unique
/// index the package says exists enforces again. A disabled index whose definition changed is replaced like any other,
/// where before it was invisible to the scan and the re-create failed on the taken name (1913).
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_IndexStateTests : BaseTableQuenchTests
{
    private static string Json(string table, string indexExtra = "", string columns = "[Code]") => $$"""
        [{
            "Schema": "dbo",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": true },
                { "Name": "Other", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "PK_{{table}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                { "Name": "UX_{{table}}_Code", "Unique": true, "IndexColumns": "{{columns}}"{{indexExtra}} }
            ]
        }]
        """;

    private static int IsDisabled(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $"SELECT CONVERT(INT, is_disabled) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = '{index}'";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? -1 : Convert.ToInt32(result);
    }

    private static string KeyColumns(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $"""
            SELECT STRING_AGG(COL_NAME(ic.[object_id], ic.column_id), ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
              FROM sys.indexes si JOIN sys.index_columns ic ON ic.[object_id] = si.[object_id] AND ic.index_id = si.index_id
             WHERE si.[object_id] = OBJECT_ID('dbo.{table}') AND si.[name] = '{index}' AND ic.is_included_column = 0
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static int AuditRows(IDbCommand cmd, string table, string index, string action)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName = '[dbo].[{table}].[{index}]' AND ActionType = '{action}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
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
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ADeclaredDisabledIndex_IsCreatedDisabled_AndARedeployLeavesItAlone()
    {
        const string table = "IdxStateDeclared";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, ", \"Disabled\": true"));
            Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(1), "the declared index exists and is disabled");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, ", \"Disabled\": true"));
            Assert.Multiple(() =>
            {
                Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(1), "still disabled after the redeploy");
                Assert.That(AuditRows(cmd, table, $"UX_{table}_Code", "modified") + AuditRows(cmd, table, $"UX_{table}_Code", "created"), Is.Zero, "the redeploy touched the index");
            });
        });
    }

    [Test]
    public void AnIndexDisabledByHand_IsRebuiltOnRedeploy_AndEnforcesUniquenessAgain()
    {
        const string table = "IdxStateByHand";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table));
            cmd.CommandText = $"ALTER INDEX UX_{table}_Code ON dbo.{table} DISABLE";
            cmd.ExecuteNonQuery();
            Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(1), "setup: the index is disabled");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table));
            Assert.Multiple(() =>
            {
                Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(0), "the package declares an enabled index, so the deploy rebuilt it");
                Assert.That(AuditRows(cmd, table, $"UX_{table}_Code", "modified"), Is.EqualTo(1), "the rebuild is audited once");
            });
            cmd.CommandText = $"INSERT INTO dbo.{table} (Id, Code) VALUES (1, 'A')";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"INSERT INTO dbo.{table} (Id, Code) VALUES (2, 'A')";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception.With.Message.Contains("duplicate key"), "the unique index enforces again");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table));
            Assert.That(AuditRows(cmd, table, $"UX_{table}_Code", "modified"), Is.Zero, "and the next deploy is a no-op");
        });
    }

    [Test]
    public void ADisabledIndexWhoseDefinitionChanged_IsReplaced()
    {
        const string table = "IdxStateChanged";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table));
            cmd.CommandText = $"ALTER INDEX UX_{table}_Code ON dbo.{table} DISABLE";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(table, columns: "[Code],[Other]"));
            Assert.Multiple(() =>
            {
                Assert.That(KeyColumns(cmd, table, $"UX_{table}_Code"), Is.EqualTo("Code,Other"), "the index carries the new key");
                Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(0), "and is enabled, as declared");
            });
        });
    }

    [Test]
    public void AnEnabledIndex_DeclaredDisabled_IsDisabled_AndWhatIfOnlyPreviewsIt()
    {
        const string table = "IdxStateWhatIf";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table));
            Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(0), "setup: enabled");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, ", \"Disabled\": true"), whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(0), "WhatIf changes nothing");
                Assert.That(AuditRows(cmd, table, $"UX_{table}_Code", "wouldModify"), Is.EqualTo(1), "WhatIf previews the disable");
            });

            RunTableQuenchProc(cmd, Json(table, ", \"Disabled\": true"));
            Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(1), "the real run disables it");
        });
    }

    [Test]
    public void IndexOnly_HonoursDisabled_BothWays()
    {
        const string table = "IdxStateIndexOnly";
        WithTable(table, cmd =>
        {
            cmd.CommandText = $"CREATE TABLE dbo.{table} (Id INT NOT NULL, Code VARCHAR(20) NULL, Other INT NULL)";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table, ", \"Disabled\": true"), indexOnly: true);
            Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(1), "index-only creates it disabled");

            RunTableQuenchProc(cmd, Json(table), indexOnly: true);
            Assert.That(IsDisabled(cmd, table, $"UX_{table}_Code"), Is.EqualTo(0), "index-only rebuilds it when the declaration drops Disabled");
        });
    }
}
