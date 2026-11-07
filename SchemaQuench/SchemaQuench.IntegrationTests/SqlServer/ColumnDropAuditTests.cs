// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// #607: a column dropped because the package no longer declares it wrote no change record on SQL Server, so the
// deployment summary, the WhatIf preview included, never showed the most destructive change a deploy can make.
[Category("SqlServer")]
public class ColumnDropAuditTests : BaseTableQuenchTests
{
    [Test]
    public void AColumnDroppedByAbsence_IsRecorded_InAPreviewAndARealRun()
    {
        const string table = "ColDropAudit";
        var json = $$"""
            [{ "Schema": "[dbo]", "Name": "[{{table}}]",
               "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[A]", "DataType": "INT", "Nullable": true } ] }]
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        try
        {
            RunTableQuenchProc(cmd, json);
            Run(cmd, $"ALTER TABLE dbo.{table} ADD [Phone] VARCHAR(20) NULL");

            Assert.That(Actions(cmd, () => RunTableQuenchProc(cmd, json, whatIf: true)), Is.EqualTo(new[] { "wouldDrop" }), "previewed");
            Assert.That(Actions(cmd, () => RunTableQuenchProc(cmd, json)), Is.EqualTo(new[] { "dropped" }), "and recorded when it runs");
        }
        finally
        {
            Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        }
    }

    private static List<string> Actions(IDbCommand cmd, System.Action deploy)
    {
        Run(cmd, "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        deploy();
        cmd.CommandText = "SELECT ActionType FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'column' AND ObjectName LIKE '%ColDropAudit%Phone%'";
        var actions = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) actions.Add(r.GetString(0));
        return actions;
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
