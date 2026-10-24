// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A table, column or index Description on MySQL and MariaDB is the engine's COMMENT, part of the object's definition: a
/// declared one is set or changed in place, as written, and "" or an omitted one clears it, as the engine's own CREATE
/// would. Comment, the old key, deploys only where Description is absent, so a blank Description beside it clears the
/// comment rather than keeping the old one.
/// </summary>
public abstract class TableQuench_DescriptionSharedTests : BaseTableQuenchTests
{
    private string Json(string table, string tableKeys, string columnKeys, string indexKeys = "") => $$"""
        [{ "Name": "{{table}}"{{tableKeys}},
           "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false{{columnKeys}} } ],
           "Indexes": [ { "Name": "ix_id", "IndexColumns": "Id"{{indexKeys}} } ] }]
        """;

    private (string Table, string Column, string Index) Comments(System.Data.IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT MAX(INDEX_COMMENT) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = 'ix_id'";
        var indexComment = cmd.ExecuteScalar()?.ToString();
        cmd.CommandText = $"SELECT TABLE_COMMENT FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'";
        var tableComment = cmd.ExecuteScalar()?.ToString();
        cmd.CommandText = $"SELECT COLUMN_COMMENT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'Id'";
        return (tableComment, cmd.ExecuteScalar()?.ToString(), indexComment);
    }

    [Test]
    public void ADescription_IsSet_ChangedAsWritten_AndClearedByAnEmptyStringOrByOmission()
    {
        var table = $"desc_{Guid.NewGuid():N}"[..16];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Keys(string description) => description == null ? "" : $", \"Description\": \"{description}\"";
        try
        {
            RunTableQuenchProc(cmd, Json(table, Keys("the orders"), Keys("the key"), Keys("by key")));
            Assert.That(Comments(cmd, table), Is.EqualTo(("the orders", "the key", "by key")), "set");

            RunTableQuenchProc(cmd, Json(table, Keys(" every order "), Keys("its key"), Keys("by its key")));
            Assert.That(Comments(cmd, table), Is.EqualTo((" every order ", "its key", "by its key")), "changed in place, as declared");

            RunTableQuenchProc(cmd, Json(table, Keys(""), Keys(""), Keys("")));
            Assert.That(Comments(cmd, table), Is.EqualTo(("", "", "")), "an empty one removes it");

            RunTableQuenchProc(cmd, Json(table, Keys("the orders"), Keys("the key"), Keys("by key")));
            RunTableQuenchProc(cmd, Json(table, Keys(null), Keys(null), Keys(null)));
            Assert.That(Comments(cmd, table), Is.EqualTo(("", "", "")), "and so does an omitted one");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{table}%'";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ABlankDescriptionBesideTheOldComment_ClearsTheComment()
    {
        var table = $"desc_{Guid.NewGuid():N}"[..16];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, ", \"Comment\": \"x\"", ", \"Comment\": \"y\""));
            Assert.That(Comments(cmd, table), Is.EqualTo(("x", "y", "")), "precondition: the old key deploys");

            RunTableQuenchProc(cmd, Json(table, ", \"Description\": \"\", \"Comment\": \"x\"", ", \"Description\": \"\", \"Comment\": \"y\""));
            Assert.That(Comments(cmd, table), Is.EqualTo(("", "", "")), "Description is the one applied, and the old key ignored");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{table}%'";
            cmd.ExecuteNonQuery();
        }
    }
}
