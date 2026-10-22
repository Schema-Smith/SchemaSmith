// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// MySQL refuses to rename a column that a functional index (3837) or a generated column (3108) reads, so a package
/// that renamed one failed the deploy. MariaDB rewrites those dependents itself; the generated-column test runs there
/// too, to show the rename still lands.
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
            cmd.CommandText = $"DROP TABLE IF EXISTS `{t}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{t}%'";
            cmd.ExecuteNonQuery();
        }
    }

    private void RequireFunctionalIndexes(System.Data.IDbCommand cmd)
    {
        if (Platform == Platform.MariaDb) Assert.Ignore("MariaDB has no functional key parts.");
        if (Convert.ToInt32(Scalar(cmd, "SELECT SchemaSmith_ServerVersionPatchNum()")) < 80013)
            Assert.Ignore("Functional key parts are MySQL 8.0.13.");
    }

    private static string Json(string t, string column, string oldName, string extraColumn, string indexes) => $$"""
        [{ "Name": "{{t}}",
           "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false },
                        { "Name": "{{column}}", "DataType": "VARCHAR(50)", "Nullable": true{{oldName}} }{{extraColumn}} ],
           "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }{{indexes}} ] }]
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
}
