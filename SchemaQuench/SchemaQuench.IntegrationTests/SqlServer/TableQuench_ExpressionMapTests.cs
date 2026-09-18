// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Expression false-change detection (#242) on SQL Server: an expression the engine rewrote on storage must
/// stop reading as a change on the next deploy.
/// <para><b>Every expression here is authored in natural form</b> — the way a person writes it, not the way
/// SQL Server stores it. A test authored in canonical form is the bug: it proves the tool agrees with itself
/// and nothing more, which is how this whole family survived a green suite.</para>
/// <para><b>The assertions are identity, not text.</b> A check constraint keeps its <c>object_id</c> and a
/// computed column its <c>column_id</c> only if it was left alone; a drop-and-recreate changes both. Counting
/// rows or matching log text would pass while the object churned underneath.</para>
/// <para><b>Both directions are covered on purpose.</b> Going quiet on churn is only half the job — a
/// metadata-only compare would also leave a hand-edited live expression in place forever, which is a worse
/// failure than the churn. The drift test is the one that pins that.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class TableQuench_ExpressionMapTests : BaseTableQuenchTests
{
    private static string CheckJson(string table, string expression) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[RetentionDays]", "DataType": "INT", "Nullable": true}
            ],
            "CheckConstraints": [
                {"Name": "[CK_{{table}}_Retention]", "Expression": "{{expression}}"}
            ]
        }
        """;

    private static string ComputedJson(string table, string expression) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Qty]", "DataType": "INT", "Nullable": false},
                {"Name": "[Doubled]", "DataType": "INT", "Nullable": true, "ComputedExpression": "{{expression}}"}
            ]
        }
        """;

    private static int ConstraintObjectId(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ISNULL((SELECT ck.[object_id] FROM sys.check_constraints ck
                                             WHERE ck.parent_object_id = OBJECT_ID('dbo.{table}')), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int ComputedColumnId(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ISNULL((SELECT c.column_id FROM sys.computed_columns c
                                             WHERE c.[object_id] = OBJECT_ID('dbo.{table}') AND c.[name] = 'Doubled'), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string LiveCheckDefinition(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ck.[definition] FROM sys.check_constraints ck
                              WHERE ck.parent_object_id = OBJECT_ID('dbo.{table}')";
        return cmd.ExecuteScalar() as string;
    }

    private static int MapRowCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Drop(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}]";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}'";
        cmd.ExecuteNonQuery();
    }

    private readonly System.Collections.Generic.List<string> _messages = new();

    private void CaptureMessages(IDbConnection conn) =>
        ((Microsoft.Data.SqlClient.SqlConnection)conn).InfoMessage += (_, e) =>
        {
            foreach (Microsoft.Data.SqlClient.SqlError err in e.Errors) _messages.Add(err.Message);
        };

    private void WithTable(string table, string createSql, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        _messages.Clear();
        CaptureMessages(conn);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            Drop(cmd, table);
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
            body(cmd);
        }
        finally { Drop(cmd, table); }
    }

    // Authored without bracket-qualifying the column, which is how people write it. SQL Server stores
    // ([RetentionDays]<=(365)); the normalizer undoes the parens and spacing but not the brackets it added,
    // so the texts never match and the constraint was dropped and re-created on every deploy.
    [Test]
    public void ACheckAuthoredWithoutBrackets_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprMapChk_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);
            var firstId = ConstraintObjectId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the constraint must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(ConstraintObjectId(cmd, table), Is.EqualTo(firstId),
                    $"pass {pass}: the constraint was dropped and re-created, so an unchanged declaration is "
                    + "still reading as a change");
            }
        });
    }

    // Computed columns have no normalization at all: authored Qty * 2 is stored as ([Qty]*(2)), so the column
    // was dropped and re-added on every deploy -- which on a PERSISTED column is a table-rewriting operation.
    [Test]
    public void AComputedColumnAuthoredInNaturalForm_IsNotDroppedAndReAddedOnEveryDeploy()
    {
        var table = $"ExprMapCol_{Guid.NewGuid():N}"[..20];
        // SQL Server reports a non-persisted computed column as NULLABLE whatever its source column says, so
        // the declaration says Nullable: true. Getting that wrong churns the column on a nullability mismatch --
        // a real difference, but not the one under test here.
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Qty] INT NOT NULL)", cmd =>
        {
            var json = ComputedJson(table, "Qty * 2");
            RunTableQuenchProc(cmd, json);
            var firstId = ComputedColumnId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the computed column must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(ComputedColumnId(cmd, table), Is.EqualTo(firstId),
                    $"pass {pass}: the computed column was dropped and re-added for an unchanged declaration");
            }
        });
    }

    // The load-bearing half. Someone edits the constraint in SSMS; the package did not change. A metadata-only
    // compare would go quiet and leave the hand-edited expression in place forever.
    [Test]
    public void AnOutOfBandEdit_IsStillReApplied()
    {
        var table = $"ExprMapDrift_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $@"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [CK_{table}_Retention];
                                 ALTER TABLE dbo.[{table}] ADD CONSTRAINT [CK_{table}_Retention] CHECK ([RetentionDays] <= 999)";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, json);

            Assert.That(LiveCheckDefinition(cmd, table), Does.Contain("365").And.Not.Contain("999"),
                "the live expression drifted from what SchemaSmith applied, so it must be re-applied -- going "
                + "quiet here would silently accept a hand-edited constraint");
        });
    }

    // The two conditions TOGETHER, which is where this went wrong: a cumulative update moves the version string
    // AND somebody hand-edited the object since the last deploy. Checking the context first made the function
    // answer "unchanged" without ever reading the live object, so the drift was left in place and the recorder
    // then wrote the DRIFTED text as the new baseline -- making it permanent and invisible. Drift wins over a
    // stale context, always.
    [Test]
    public void AStaleContextDoesNotExcuseDrift_TheObjectIsStillReApplied()
    {
        var table = $"ExprMapCtxDrift_{Guid.NewGuid():N}"[..24];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);

            // somebody edits the constraint by hand ...
            cmd.CommandText = $@"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [CK_{table}_Retention];
                                 ALTER TABLE dbo.[{table}] ADD CONSTRAINT [CK_{table}_Retention] CHECK ([RetentionDays] <= 999)";
            cmd.ExecuteNonQuery();
            // ... and a cumulative update lands before the next deploy
            cmd.CommandText = $"UPDATE SchemaSmith.ExpressionMap SET [EngineVersion] = 'from-another-server' WHERE [ObjectTable] = '{table}'";
            Assert.That(cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist");

            RunTableQuenchProc(cmd, json);

            Assert.That(LiveCheckDefinition(cmd, table), Does.Contain("365").And.Not.Contain("999"),
                "a version change must not excuse drift -- the declared expression must be re-applied");
            cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap
                                  WHERE [ObjectTable] = '{table}' AND [CanonicalText] LIKE '%999%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "and the drifted text must never become the recorded baseline");
        });
    }

    // A mapping is only trustworthy for the context it was written in: SQL Server freezes an expression's
    // stored text at creation-time compatibility level. A row whose context no longer matches is stale, not
    // wrong -- re-read and rewrite it, and do NOT touch the object, which would drop and recreate every
    // expression-bearing object in the database on one deploy.
    [Test]
    public void AStaleContextRow_IsReBaselined_WithoutTouchingTheObject()
    {
        var table = $"ExprMapCtx_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            var json = CheckJson(table, "RetentionDays <= 365");
            RunTableQuenchProc(cmd, json);
            var firstId = ConstraintObjectId(cmd, table);

            cmd.CommandText = $@"UPDATE SchemaSmith.ExpressionMap
                                    SET [EngineVersion] = 'from-another-server'
                                  WHERE [ObjectTable] = '{table}'";
            Assert.That(cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist to go stale (only the CONTEXT goes stale -- the recorded canonical text still matches the live object, which is what a real engine upgrade looks like)");

            _messages.Clear();
            RunTableQuenchProc(cmd, json);

            Assert.That(ConstraintObjectId(cmd, table), Is.EqualTo(firstId),
                "a stale context must re-baseline, never re-apply");
            Assert.That(_messages.FindAll(m => m.Contains("Re-baselined 1 recorded expression")), Has.Count.EqualTo(1),
                "a re-baseline must be reported in the deploy log, not happen silently: "
                + string.Join(" | ", _messages.FindAll(m => m.Contains("aseline"))));
            cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap
                                  WHERE [ObjectTable] = '{table}' AND [EngineVersion] = 'from-another-server'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "the stale row must be rewritten with the current context");
        });
    }

    [Test]
    public void ApplyingAnExpression_RecordsWhatWasApplied()
    {
        var table = $"ExprMapRec_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            RunTableQuenchProc(cmd, CheckJson(table, "RetentionDays <= 365"));

            Assert.That(MapRowCount(cmd, table), Is.EqualTo(1), "the applied expression must be recorded");
            cmd.CommandText = $@"SELECT [AuthoredText] + '|' + [CanonicalText] FROM SchemaSmith.ExpressionMap
                                  WHERE [ObjectTable] = '{table}'";
            var row = (string)cmd.ExecuteScalar();
            Assert.That(row, Does.StartWith("RetentionDays <= 365|"), "the authored text is stored as written: " + row);
            Assert.That(row, Does.Contain("[RetentionDays]"), "and the canonical text as the engine reports it: " + row);
        });
    }

    // A change to the declaration must still re-apply: the mapping is an optimisation over a working
    // comparison, never a way to stop noticing a real edit.
    [Test]
    public void AChangedDeclaration_IsStillApplied()
    {
        var table = $"ExprMapChg_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([RetentionDays] INT NULL)", cmd =>
        {
            RunTableQuenchProc(cmd, CheckJson(table, "RetentionDays <= 365"));
            RunTableQuenchProc(cmd, CheckJson(table, "RetentionDays <= 730"));

            Assert.That(LiveCheckDefinition(cmd, table), Does.Contain("730"),
                "an edited declaration must reach the server");
        });
    }

    // ------------------------------------------------------------------------------------------------------------
    // The measured case the re-baseline rule exists for, done for real rather than simulated by editing a row.
    // SQL Server freezes an expression's stored text at the compatibility level it was CREATED under: at compat 100
    // CONVERT(varchar(10), Qty) is stored as the 3-argument CONVERT(...,0), and it stays that way after the database
    // is raised to the server's own level (which one that is differs per version -- 150 on 2019, 160 on 2022 -- so
    // it is read from the server rather than hardcoded; hardcoding 160 failed CI's 2019 leg with "Valid values of
    // the database compatibility level are 100, 110, 120, 130, 140 or 150"). Re-applying on the context change would drop and re-create every expression-bearing object
    // in the database on one deploy. Compat 100 is below the OPENJSON cliff, so this deploys through the XML ingest
    // path, exactly as production does for such a database.
    // ------------------------------------------------------------------------------------------------------------
    [Test]
    public void ARealCompatibilityLevelChange_ReBaselines_AndTouchesNothing()
    {
        var db = $"ExprMapCompat_{Guid.NewGuid():N}"[..28];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        _messages.Clear();
        CaptureMessages(conn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"CREATE DATABASE [{db}]";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT [compatibility_level] FROM sys.databases WHERE [name] = 'model'";
            var serverCompat = Convert.ToInt32(cmd.ExecuteScalar());
            if (serverCompat <= 100)
                Assert.Ignore($"The server's own compatibility level is {serverCompat}; there is no upgrade from 100 to measure.");
            cmd.CommandText = $"ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = 100";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(db);
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, encoding: IngestEncoding.Xml);

            cmd.CommandText = "CREATE TABLE dbo.CompatProbe ([Qty] INT NULL)";
            cmd.ExecuteNonQuery();

            const string tableJson = """
                [{
                    "Schema": "[dbo]",
                    "Name": "[CompatProbe]",
                    "Columns": [ {"Name": "[Qty]", "DataType": "INT", "Nullable": true} ],
                    "CheckConstraints": [ {"Name": "[CK_CompatProbe_Qty]", "Expression": "CONVERT(varchar(10), Qty) <> ''"} ]
                }]
                """;

            void DeployViaXml()
            {
                var xml = ModelXmlSerializer.ToIngestXml(tableJson, "Tables", "Table");
                cmd.CommandText = "DECLARE @TableDefinitions XML = @payload;\nDECLARE @UpdateFillFactor BIT = 0;\n"
                                  + ForgeKindler.GetParseTableXmlScript(Platform.SqlServer)
                                  + "\nEXEC SchemaSmith.MissingTableAndColumnQuench @WhatIf = 0"
                                  + "\nEXEC SchemaSmith.ModifiedTableQuench @ProductName = 'CompatProbe', @WhatIf = 0, @DropUnknownIndexes = 0, @DropTablesRemovedFromProduct = 0"
                                  + "\nEXEC SchemaSmith.MissingIndexesAndConstraintsQuench 'CompatProbe', 0"
                                  + "\nEXEC SchemaSmith.ExpressionMapRecord @WhatIf = 0";
                cmd.Parameters.Clear();
                var payload = cmd.CreateParameter();
                payload.ParameterName = "@payload";
                payload.Value = xml;
                cmd.Parameters.Add(payload);
                cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();
            }

            int ObjectId()
            {
                cmd.CommandText = "SELECT ISNULL(OBJECT_ID('dbo.CK_CompatProbe_Qty'), 0)";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }

            string StoredDefinition()
            {
                cmd.CommandText = "SELECT [definition] FROM sys.check_constraints WHERE [name] = 'CK_CompatProbe_Qty'";
                return cmd.ExecuteScalar() as string;
            }

            string MapRow()
            {
                cmd.CommandText = @"SELECT CAST([CompatLevel] AS VARCHAR(10)) + '|' + [CanonicalText] FROM SchemaSmith.ExpressionMap
                                     WHERE [ObjectTable] = 'CompatProbe' AND [ObjectName] = 'CK_CompatProbe_Qty'";
                return cmd.ExecuteScalar() as string;
            }

            // Pass 1, at compat 100.
            DeployViaXml();
            var id = ObjectId();
            Assert.That(id, Is.Not.Zero, "setup: the constraint must exist");
            var frozen = StoredDefinition();
            Assert.That(frozen, Does.Contain(",0)"),
                "precondition: at compat 100 SQL Server stores the 3-argument CONVERT -- without that this proves nothing: " + frozen);
            Assert.That(MapRow(), Does.StartWith("100|"), "the mapping must record the compat level it was written under");

            // The upgrade.
            conn.ChangeDatabase("master");
            cmd.CommandText = $"ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = {serverCompat}";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(db);

            // Pass 2, at the server's compatibility level: re-baseline, report it, change nothing.
            _messages.Clear();
            DeployViaXml();
            var afterUpgradeId = ObjectId();
            var afterUpgradeDef = StoredDefinition();
            var afterUpgradeRow = MapRow();
            var reported = _messages.FindAll(m => m.Contains("Re-baselined 1 recorded expression"));
            Assert.Multiple(() =>
            {
                Assert.That(afterUpgradeId, Is.EqualTo(id), "a compatibility-level change must NOT re-create the constraint");
                Assert.That(afterUpgradeDef, Is.EqualTo(frozen), "the stored text must be untouched");
                Assert.That(afterUpgradeRow, Does.StartWith($"{serverCompat}|"), "the mapping must now carry the new compat level");
                Assert.That(reported, Has.Count.EqualTo(1),
                    "the re-baseline must be reported: " + string.Join(" | ", _messages.FindAll(m => m.Contains("aseline"))));
            });

            // Pass 3: context matches again -- nothing to report, nothing re-created.
            _messages.Clear();
            DeployViaXml();
            var pass3Id = ObjectId();
            var pass3Reported = _messages.FindAll(m => m.Contains("Re-baselined"));
            Assert.Multiple(() =>
            {
                Assert.That(pass3Id, Is.EqualTo(id), "pass 3 must leave the constraint alone");
                Assert.That(pass3Reported, Is.Empty, "pass 3 has nothing to re-baseline");
            });
        }
        finally
        {
            conn.ChangeDatabase("master");
            cmd.Parameters.Clear();
            cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            cmd.ExecuteNonQuery();
        }
    }
}
