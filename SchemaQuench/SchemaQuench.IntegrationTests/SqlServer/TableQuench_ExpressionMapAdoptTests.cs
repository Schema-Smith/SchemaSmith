// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// #508: a database another tool built holds every expression in SQL Server's own words and has no expression-map
/// rows, so its first SchemaSmith deploy re-applied every CHECK, computed column, default and filter whose stored text
/// differs from the declared text -- a re-validation under a schema lock, a filtered unique index dropped and rebuilt.
/// The declared expression is now rendered on a scratch copy first; if SQL Server stores it as exactly the live text,
/// the object is recorded, not re-applied. Every table here is built by hand, the way another tool would build it,
/// and declared in natural form (Rule 32). The assertions are identity and the audit, not text.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ExpressionMapAdoptTests : BaseTableQuenchTests
{
    private static string Create(string t, string parked = "'AwaitingCompanyMapping', 'AwaitingRoleMapping'") => $"""
        CREATE TABLE dbo.[{t}] ([Id] INT NOT NULL, [ParkedReason] VARCHAR(40) NULL, [Status] INT NULL, [Code] VARCHAR(10) NULL,
          [IdText] AS (CONVERT(VARCHAR(10), [Id])),
          [Created] DATETIME NULL CONSTRAINT [DF_{t}_Created] DEFAULT (GETDATE()),
          CONSTRAINT [CK_{t}_Parked] CHECK ([ParkedReason] IN ({parked})));
        CREATE UNIQUE INDEX [UX_{t}_Code] ON dbo.[{t}] ([Code]) WHERE [Status] IN (1, 2);
        CREATE STATISTICS [ST_{t}_Status] ON dbo.[{t}] ([Status]) WHERE [Status] IN (1, 2);
        """;

    private static string Json(string t, string parked = "'AwaitingCompanyMapping', 'AwaitingRoleMapping'") => $$"""
        [{ "Schema": "[dbo]", "Name": "[{{t}}]",
           "Columns": [
             { "Name": "[Id]", "DataType": "INT", "Nullable": false },
             { "Name": "[ParkedReason]", "DataType": "VARCHAR(40)", "Nullable": true },
             { "Name": "[Status]", "DataType": "INT", "Nullable": true },
             { "Name": "[Code]", "DataType": "VARCHAR(10)", "Nullable": true },
             { "Name": "[IdText]", "DataType": "VARCHAR(10)", "Nullable": true, "ComputedExpression": "CONVERT(VARCHAR(10), [Id])" },
             { "Name": "[Created]", "DataType": "DATETIME", "Nullable": true, "Default": "GETDATE()" } ],
           "Indexes": [ { "Name": "[UX_{{t}}_Code]", "Unique": true, "IndexColumns": "[Code]", "FilterExpression": "[Status] IN (1, 2)" } ],
           "Statistics": [ { "Name": "[ST_{{t}}_Status]", "Columns": "[Status]", "FilterExpression": "[Status] IN (1, 2)" } ],
           "CheckConstraints": [ { "Name": "[CK_{{t}}_Parked]", "Expression": "[ParkedReason] IN ({{parked}})" } ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    // object_id of the CHECK and the default, column_id of the computed column: each changes on a drop and re-create.
    private static string Identity(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT CAST(OBJECT_ID('dbo.CK_{t}_Parked') AS VARCHAR(20)) + '|' + CAST(OBJECT_ID('dbo.DF_{t}_Created') AS VARCHAR(20)) + '|' +
               CAST(COLUMNPROPERTY(OBJECT_ID('dbo.{t}'), 'IdText', 'ColumnId') AS VARCHAR(20))
        """);

    private static string Audits(IDbCommand cmd, string t) => Scalar(cmd, $"""
        SELECT ISNULL(STUFF((SELECT ', ' + ObjectType + ':' + ActionType FROM SchemaSmith.ChangeAudit
                              WHERE SessionId = @@SPID AND ObjectName LIKE '%{t}%' ORDER BY ObjectType FOR XML PATH('')), 1, 2, ''), '')
        """);

    private static int MapRows(IDbCommand cmd, string t) => Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{t}'"));

    private void WithTable(string t, Action<IDbCommand, List<string>> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var messages = new List<string>();
        ((SqlConnection)conn).InfoMessage += (_, e) => messages.Add(e.Message);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Drop()
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{t}]; DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{t}'; DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
        }
        Drop();
        try { body(cmd, messages); }
        finally { Drop(); }
    }

    [Test]
    public void ADatabaseBuiltElsewhere_HasNothingReApplied_OnItsFirstDeploy_AndTheSecondIsANoOp()
    {
        var t = $"Adopt_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, messages) =>
        {
            cmd.CommandText = Create(t);
            cmd.ExecuteNonQuery();
            var before = Identity(cmd, t);

            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "nothing re-applied: the CHECK, computed column, default, filtered index and filtered statistic are the declared ones");
                Assert.That(Identity(cmd, t), Is.EqualTo(before), "the CHECK, the default and the computed column are the same objects");
                Assert.That(messages, Has.Some.Contains("Verified 5 expression(s) on a scratch copy"), "and the deploy says what it verified");
                Assert.That(MapRows(cmd, t), Is.EqualTo(5));
            });

            messages.Clear();
            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "the second deploy is a no-op");
                Assert.That(messages, Has.None.Contains("on a scratch copy"), "and the map answers, without a probe");
            });
        });
    }

    // The half that must not go quiet: the live CHECK holds one value where the package declares two. The declared text
    // renders to something else, so the live object is not the declared one and is re-applied.
    [Test]
    public void ARealDifference_IsStillReApplied()
    {
        var t = $"AdoptDrift_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            cmd.CommandText = Create(t, parked: "'AwaitingCompanyMapping'");
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT [definition] FROM sys.check_constraints WHERE [name] = 'CK_{t}_Parked'"), Does.Contain("AwaitingRoleMapping"),
                    "the declared CHECK replaced the live one");
                Assert.That(Audits(cmd, t), Is.EqualTo("constraint:dropped, constraint:created"), "and only it was touched");
            });
        });
    }

    // A declaration reworded without changing its meaning renders to the same stored text, so it is not re-applied either.
    [Test]
    public void AReformattedDeclaration_IsNotReApplied()
    {
        var t = $"AdoptReword_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            RunTableQuenchProc(cmd, Json(t));
            var before = Identity(cmd, t);
            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(t, parked: "'AwaitingCompanyMapping','AwaitingRoleMapping'").Replace("CONVERT(VARCHAR(10), [Id])", "convert(varchar(10),Id)"));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty);
                Assert.That(Identity(cmd, t), Is.EqualTo(before));
            });
        });
    }

    // A preview shows what the real deploy would do -- nothing -- and leaves the map as it found it.
    [Test]
    public void WhatIf_ReportsNothingToDo_AndLeavesTheMapAsItWas()
    {
        var t = $"AdoptWhatIf_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            cmd.CommandText = Create(t);
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(t), whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "no would-change for an expression the database already holds");
                Assert.That(MapRows(cmd, t), Is.Zero, "the preview wrote nothing to the map");
            });
        });
    }

    // The index-only deploy compares filters the same way, so it checks them the same way.
    [Test]
    public void AnIndexOnlyDeploy_DoesNotRebuildAFilteredIndexBuiltElsewhere()
    {
        var t = $"AdoptIdx_{Guid.NewGuid():N}"[..20];
        WithTable(t, (cmd, _) =>
        {
            cmd.CommandText = Create(t);
            cmd.ExecuteNonQuery();
            // The index's storage id is new after a drop and re-create; the index-only path writes no audit row to read.
            var hobt = $"SELECT p.hobt_id FROM sys.partitions p JOIN sys.indexes i ON i.[object_id] = p.[object_id] AND i.index_id = p.index_id WHERE i.[name] = 'UX_{t}_Code'";
            var before = Scalar(cmd, hobt);
            RunTableQuenchProc(cmd, Json(t), indexOnly: true);
            Assert.That(Scalar(cmd, hobt), Is.EqualTo(before), "the filtered index is the declared one");
        });
    }
}
