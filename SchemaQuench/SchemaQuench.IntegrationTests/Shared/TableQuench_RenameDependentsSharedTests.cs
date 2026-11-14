// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// MySQL refuses to rename a column that a functional index (3837), a generated column (3108) or a check constraint (3959)
/// reads, so a package that renamed one failed the deploy. MariaDB rewrites those dependents itself; the generated-column
/// and check tests run there too, to show the rename still lands.
/// </summary>
public abstract class TableQuench_RenameDependentsSharedTests : BaseTableQuenchTests
{
    private string Scalar(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void WithTable(Action<System.Data.IDbCommand, string> body)
    {
        var t = $"rn_{Guid.NewGuid():N}"[..14];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            body(cmd, t);
        }
        finally
        {
            // The ownership table is shared by every worker's deploy, so this delete can be a deadlock victim (MySQL 8.4 band).
            cmd.CommandText = $"DROP TABLE IF EXISTS `{t}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{t}%'";
            RetryingTransientConcurrency(() => cmd.ExecuteNonQuery());
        }
    }

    private void RequireFunctionalIndexes(System.Data.IDbCommand cmd)
    {
        if (Platform == Platform.MariaDb) Assert.Ignore("MariaDB has no functional key parts.");
        if (Convert.ToInt32(Scalar(cmd, "SELECT SchemaSmith_ServerVersionPatchNum()")) < 80013)
            Assert.Ignore("Functional key parts are MySQL 8.0.13.");
    }

    private void RequireCheckConstraints(System.Data.IDbCommand cmd)
    {
        if (Scalar(cmd, "SELECT SchemaSmith_SupportsCheckConstraints()") != "1")
            Assert.Ignore("Enforced check constraints are MySQL 8.0.16 / MariaDB 10.2.22.");
    }

    private static string Json(string t, string column, string oldName, string extraColumn, string indexes, string checks = "") => $$"""
        [{ "Name": "{{t}}",
           "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false },
                        { "Name": "{{column}}", "DataType": "VARCHAR(50)", "Nullable": true{{oldName}} }{{extraColumn}} ],
           "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }{{indexes}} ],
           "CheckConstraints": [ {{checks}} ] }]
        """;

    [Test]
    public void AColumnAFunctionalIndexReads_IsRenamed_AndTheIndexReadsTheNewName()
    {
        WithTable((cmd, t) =>
        {
            RequireFunctionalIndexes(cmd);
            RunTableQuenchProc(cmd, Json(t, "Email", "", "", """, { "Name": "ix_lower", "IndexColumns": "(lower(`Email`))" }"""));
            RunTableQuenchProc(cmd, Json(t, "Mail", """, "OldName": "Email" """, "", """, { "Name": "ix_lower", "IndexColumns": "(lower(`Mail`))" }"""));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}'"),
                    Is.EqualTo("Id,Mail"), "renamed");
                Assert.That(Scalar(cmd, $"SELECT EXPRESSION FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}' AND INDEX_NAME = 'ix_lower'"),
                    Is.EqualTo("lower(`Mail`)"), "and the index is back over the new name");
            });
        });
    }

    [Test]
    public void AColumnAnUndeclaredFunctionalIndexReads_StopsTheDeployByName_BeforeRenaming()
    {
        WithTable((cmd, t) =>
        {
            RequireFunctionalIndexes(cmd);
            RunTableQuenchProc(cmd, Json(t, "Email", "", "", ""));
            cmd.CommandText = $"CREATE INDEX ix_mine ON `{t}` ((lower(Email)))";
            cmd.ExecuteNonQuery();
            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, Json(t, "Mail", """, "OldName": "Email" """, "", "")));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("ix_mine"));
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}'"),
                    Is.EqualTo("Id,Email"), "nothing renamed");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}' AND INDEX_NAME = 'ix_mine'"),
                    Is.EqualTo("1"), "and the index is still there");
            });
        });
    }

    [Test]
    public void AColumnAGeneratedColumnReads_IsRenamed_AndTheGeneratedColumnReadsTheNewName()
    {
        WithTable((cmd, t) =>
        {
            string Gen(string col) => $$""", { "Name": "Len", "DataType": "INT", "Nullable": true, "GenerationExpression": "char_length(`{{col}}`)", "Generated": "VIRTUAL" }""";
            RunTableQuenchProc(cmd, Json(t, "Email", "", Gen("Email"), ""));
            cmd.CommandText = $"INSERT INTO `{t}` (Id, Email) VALUES (1, 'abc')";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(t, "Mail", """, "OldName": "Email" """, Gen("Mail"), ""));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT CONCAT(Mail, ':', Len) FROM `{t}` WHERE Id = 1"), Is.EqualTo("abc:3"), "renamed, data kept, and the generated column computes from it");
                Assert.That(Scalar(cmd, $"SELECT GENERATION_EXPRESSION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}' AND COLUMN_NAME = 'Len'"),
                    Does.Contain("Mail"));
            });
        });
    }

    // MySQL refuses the rename while a check reads the column (3959); MariaDB rewrites the check itself.
    [Test]
    public void AColumnACheckReads_IsRenamed_AndTheCheckReadsTheNewName()
    {
        WithTable((cmd, t) =>
        {
            RequireCheckConstraints(cmd);
            string Check(string col) => $$"""{ "Name": "ck_{{t}}", "Expression": "char_length(`{{col}}`) < 40" }""";
            RunTableQuenchProc(cmd, Json(t, "Email", "", "", "", Check("Email")));
            cmd.CommandText = $"INSERT INTO `{t}` (Id, Email) VALUES (1, 'abc')";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(t, "Mail", """, "OldName": "Email" """, "", "", Check("Mail")));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT Mail FROM `{t}` WHERE Id = 1"), Is.EqualTo("abc"), "renamed, data kept");
                Assert.That(Scalar(cmd, $"SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'ck_{t}'"),
                    Does.Contain("Mail"), "and the check is back over the new name");
            });
            cmd.CommandText = $"INSERT INTO `{t}` (Id, Mail) VALUES (2, REPEAT('x', 45))";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception, "and enforced");
        });
    }

    [Test]
    public void AColumnAnUndeclaredCheckReads_StopsTheDeployByName_BeforeRenaming()
    {
        WithTable((cmd, t) =>
        {
            RequireCheckConstraints(cmd);
            if (Platform == Platform.MariaDb) Assert.Ignore("MariaDB renames the column under the check; nothing is dropped.");
            RunTableQuenchProc(cmd, Json(t, "Email", "", "", ""));
            cmd.CommandText = $"ALTER TABLE `{t}` ADD CONSTRAINT ck_mine_{t} CHECK (Email <> '')";
            cmd.ExecuteNonQuery();
            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, Json(t, "Mail", """, "OldName": "Email" """, "", "")));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("undeclared check").And.Contain($"ck_mine_{t}"), "the deploy's own refusal, not the engine's 3959");
                Assert.That(Scalar(cmd, $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}'"),
                    Is.EqualTo("Id,Email"), "nothing renamed");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'ck_mine_{t}'"),
                    Is.EqualTo("1"), "and the check is still there");
            });
        });
    }
}
