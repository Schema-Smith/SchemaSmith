// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// #508 on MySQL and MariaDB: a table another tool built holds its CHECK, generated column and expression default in
/// the engine's own words (charset introducers and backticks on MySQL, lower case on MariaDB) and has no expression-map
/// rows, so the first deploy re-applied each one. The declared expression is now rendered on a scratch copy first and
/// recorded when the engine stores it as exactly the live text. Built by hand, declared in natural form; these engines
/// give an object no stable identity, so the arbiter is the change audit.
/// </summary>
public abstract class TableQuench_ExpressionMapAdoptSharedTests : BaseTableQuenchTests
{
    // MariaDB keeps a check written on a column as that column's own; MySQL makes every check table-level.
    private bool ColumnLevelCheck => Platform == Platform.MariaDb;

    private string Create(string t, string check = "`Id` > 0 AND `Tag` <> 'x'", string generated = "CONCAT(`Tag`, '-x')", string codeDefault = "CONCAT('a', 'b')") => $"""
        CREATE TABLE `{_mainDb}`.`{t}` (`Id` INT NOT NULL, `Tag` VARCHAR(50) NULL, `Qty` INT NULL{(ColumnLevelCheck ? " CHECK (`Qty` >= 0)" : "")},
          `Label` VARCHAR(60) GENERATED ALWAYS AS ({generated}) STORED,
          `Code` VARCHAR(20) NULL DEFAULT ({codeDefault}),
          PRIMARY KEY (`Id`), CONSTRAINT `ck_{t}` CHECK ({check}))
        """;

    private string Json(string t, string check = "Id > 0 AND Tag <> 'x'") => $$"""
        [{ "Name": "{{t}}",
           "Columns": [
             { "Name": "Id", "DataType": "INT", "Nullable": false },
             { "Name": "Tag", "DataType": "VARCHAR(50)", "Nullable": true },
             { "Name": "Qty", "DataType": "INT", "Nullable": true{{(ColumnLevelCheck ? ", \"CheckExpression\": \"Qty >= 0\"" : "")}} },
             { "Name": "Label", "DataType": "VARCHAR(60)", "Nullable": true, "GenerationExpression": "CONCAT(Tag, '-x')", "Generated": "STORED" },
             { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": true, "Default": "CONCAT('a', 'b')" } ],
           "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
           "CheckConstraints": [ { "Name": "ck_{{t}}", "Expression": "{{check}}" } ] }]
        """;

    private int Expected => ColumnLevelCheck ? 4 : 3;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string Audits(IDbCommand cmd, string t) => Scalar(cmd,
        $"SELECT IFNULL(GROUP_CONCAT(CONCAT(ObjectType, ':', ActionType) ORDER BY ObjectType, ActionType SEPARATOR ', '), '') FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{t}%'");

    private static int Verified(IDbCommand cmd, int count) => Convert.ToInt32(Scalar(cmd,
        $"SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%Verified {count} expression(s) on a scratch copy%'"));

    private static int MapRows(IDbCommand cmd, string t) => Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith_ExpressionMap WHERE ObjectTable = '{t}'"));

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void WithTable(string t, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints and expression defaults need MySQL 8.0.16 / MariaDB 10.2.");
        void Drop() => Exec(cmd, $"""
            DROP TABLE IF EXISTS `{_mainDb}`.`{t}`; DELETE FROM SchemaSmith_ExpressionMap WHERE ObjectTable = '{t}';
            DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectSchema = '{_mainDb}' AND ObjectName LIKE '{t}%';
            DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID(); DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()
            """);
        Drop();
        try { body(cmd); }
        finally { Drop(); }
    }

    [Test]
    public void ATableBuiltElsewhere_HasNothingReApplied_OnItsFirstDeploy_AndTheSecondIsANoOp()
    {
        var t = $"adopt_{Guid.NewGuid():N}"[..20];
        WithTable(t, cmd =>
        {
            Exec(cmd, Create(t));
            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "nothing re-applied: the CHECK, generated column and default are the declared ones");
                Assert.That(Verified(cmd, Expected), Is.EqualTo(1), "and the deploy says what it verified");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME LIKE '\\_schemasmith\\_probe\\_%'"),
                    Is.EqualTo("0"), "and leaves no copy behind");
            });

            Exec(cmd, "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()");
            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty, "the second deploy is a no-op");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%on a scratch copy%'"),
                    Is.EqualTo("0"), "and the map answers, without a probe");
            });
        });
    }

    [Test]
    public void ARealDifference_IsStillReApplied()
    {
        var t = $"adoptdrift_{Guid.NewGuid():N}"[..20];
        WithTable(t, cmd =>
        {
            Exec(cmd, Create(t, check: "`Id` > 5 AND `Tag` <> 'x'"));
            RunTableQuenchProc(cmd, Json(t));
            Assert.That(Audits(cmd, t), Is.EqualTo("constraint:created, constraint:dropped"), "the CHECK, and only it, is re-applied");
        });
    }

    // The verification must not bless a difference: a generated column or default that is not the declared one is applied.
    [Test]
    public void ARealDifference_InAGeneratedColumnAndADefault_IsStillReApplied()
    {
        var t = $"adoptcol_{Guid.NewGuid():N}"[..20];
        WithTable(t, cmd =>
        {
            Exec(cmd, Create(t, generated: "CONCAT(`Tag`, '-y')", codeDefault: "CONCAT('a', 'c')"));
            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT GENERATION_EXPRESSION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{t}' AND COLUMN_NAME = 'Label'"),
                    Does.Contain("-x"), "the declared generation expression is applied");
                Assert.That(Scalar(cmd, $"SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{t}' AND COLUMN_NAME = 'Code'").Replace("\\'", "'"),
                    Does.Contain("'b'").And.Not.Contain("'c'"), "the declared default is applied");
            });
        });
    }

    [Test]
    public void AReformattedDeclaration_IsNotReApplied()
    {
        var t = $"adoptreword_{Guid.NewGuid():N}"[..20];
        WithTable(t, cmd =>
        {
            RunTableQuenchProc(cmd, Json(t));
            Exec(cmd, "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()");
            // Spacing and keyword case only: a column written in another case is stored as written, which is a real difference.
            RunTableQuenchProc(cmd, Json(t, check: "Id>0   and   Tag<>'x'"));
            Assert.That(Audits(cmd, t), Is.Empty);
        });
    }

    // A row recorded under another rendering of the same declaration: a 2.7.x session with ANSI_QUOTES had MariaDB report
    // "Id" where SchemaSmith's own sessions now read `Id` (SP-5 finding 9). The live text no longer matches the row, which
    // reads as drift; the declared text renders to the live text on the copy, so the row is re-recorded, not re-applied.
    [Test]
    public void ARowRecordedUnderAnotherRendering_IsReRecorded_NotReApplied()
    {
        var t = $"adoptstale_{Guid.NewGuid():N}"[..20];
        WithTable(t, cmd =>
        {
            RunTableQuenchProc(cmd, Json(t));
            Exec(cmd, $"UPDATE SchemaSmith_ExpressionMap SET CanonicalText = REPLACE(CanonicalText, '`', '\"') WHERE ObjectTable = '{t}'");
            Exec(cmd, "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()");

            RunTableQuenchProc(cmd, Json(t));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, t), Is.Empty);
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith_ExpressionMap WHERE ObjectTable = '{t}' AND CanonicalText LIKE '%\"%'"), Is.EqualTo("0"),
                    "and every row now holds the text the engine reports");
            });
        });
    }

    // The copy is a real table on these engines, and WhatIf has never written DDL: a preview skips the check, leaves
    // nothing behind and reports such an object as it always has.
    [Test]
    public void WhatIf_SkipsTheCheck_AndWritesNothing()
    {
        var t = $"adoptwhatif_{Guid.NewGuid():N}"[..20];
        WithTable(t, cmd =>
        {
            Exec(cmd, Create(t));
            RunTableQuenchProc(cmd, Json(t), whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(MapRows(cmd, t), Is.Zero);
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%on a scratch copy%'"), Is.EqualTo("0"));
            });
        });
    }
}
