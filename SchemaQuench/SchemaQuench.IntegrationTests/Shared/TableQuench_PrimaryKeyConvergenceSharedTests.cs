// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

// An existing table takes the primary key its package declares: added where it has none, replaced where the columns
// differ, on a full deploy and an index-only one. A key the package does not declare stays.
public abstract class TableQuench_PrimaryKeyConvergenceSharedTests : BaseTableQuenchTests
{
    private static string Json(string table, string primaryKey, bool autoIncrement = false) => $$"""
        [{
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false{{(autoIncrement ? ", \"AutoIncrement\": true" : "")}} },
                { "Name": "Tenant", "DataType": "INT", "Nullable": false },
                { "Name": "Val", "DataType": "VARCHAR(10)", "Nullable": true }
            ],
            "Indexes": [{{(primaryKey == null ? "" : $$"""{ "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{primaryKey}}" }""")}}]
        }]
        """;

    private void WithTable(string prefix, string createSql, Action<IDbCommand, string> body)
    {
        var table = $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"USE `{_mainDb}`; CREATE TABLE `{table}` {createSql}; INSERT INTO `{table}` (Id, Tenant, Val) VALUES (1, 1, 'a'), (2, 1, 'b')";
        cmd.ExecuteNonQuery();
        try
        {
            body(cmd, table);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    private string PrimaryKey(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'PRIMARY'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string Audit(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COALESCE(GROUP_CONCAT(CONCAT(ObjectType, ' ', ActionType) ORDER BY Id), '') FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '{table}.%'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private static int Rows(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM `{table}`";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AKeylessTable_TakesTheDeclaredPrimaryKey_AndARedeployIsANoOp(bool indexOnly)
    {
        WithTable("PkAdd", "(Id INT NOT NULL, Tenant INT NOT NULL, Val VARCHAR(10))", (cmd, table) =>
        {
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "`Id`"), indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("Id"), "the declared key is added");
                Assert.That(Audit(cmd, table), Is.EqualTo("index created"));
                Assert.That(Rows(cmd, table), Is.EqualTo(2));
            });

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "`Id`"), indexOnly);
            Assert.That(Audit(cmd, table), Is.Empty, "an equal key is left alone");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AChangedPrimaryKey_IsReplaced_KeepingTheRows(bool indexOnly)
    {
        WithTable("PkChange", "(Id INT NOT NULL, Tenant INT NOT NULL, Val VARCHAR(10), PRIMARY KEY (Id))", (cmd, table) =>
        {
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "`Tenant`,`Id`"), indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("Tenant,Id"));
                Assert.That(Audit(cmd, table), Is.EqualTo("index dropped,index created"));
                Assert.That(Rows(cmd, table), Is.EqualTo(2));
            });

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "`Tenant`,`Id`"), indexOnly);
            Assert.That(Audit(cmd, table), Is.Empty);
        });
    }

    [Test]
    public void AnAutoIncrementColumn_MovedOffTheFrontOfTheKey_GetsAKeyOfItsOwn()
    {
        WithTable("PkAutoInc", "(Id INT NOT NULL AUTO_INCREMENT, Tenant INT NOT NULL, Val VARCHAR(10), PRIMARY KEY (Id))", (cmd, table) =>
        {
            RunTableQuenchProc(cmd, Json(table, "`Tenant`,`Id`", autoIncrement: true));
            Assert.That(PrimaryKey(cmd, table), Is.EqualTo("Tenant,Id"));
            cmd.CommandText = $"INSERT INTO `{table}` (Tenant, Val) VALUES (2, 'c'); SELECT MAX(Id) FROM `{table}`";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3), "the counter carries on");
        });
    }

    [Test]
    public void WhatIf_ReportsTheKey_AndChangesNothing()
    {
        WithTable("PkWhatIf", "(Id INT NOT NULL, Tenant INT NOT NULL, Val VARCHAR(10), PRIMARY KEY (Id))", (cmd, table) =>
        {
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "`Tenant`,`Id`"), whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("Id"));
                Assert.That(Audit(cmd, table), Is.EqualTo("index wouldDrop,index wouldCreate"));
            });
        });
    }

    [Test]
    public void AnUndeclaredPrimaryKey_Stays()
    {
        WithTable("PkKeep", "(Id INT NOT NULL, Tenant INT NOT NULL, Val VARCHAR(10), PRIMARY KEY (Id))", (cmd, table) =>
        {
            RunTableQuenchProc(cmd, Json(table, null));
            Assert.That(PrimaryKey(cmd, table), Is.EqualTo("Id"));
        });
    }

    [Test]
    public void AKeyAForeignKeyNeeds_IsKept_AndTheDeployCarriesOn()
    {
        WithTable("PkFkParent", "(Id INT NOT NULL, Tenant INT NOT NULL, Val VARCHAR(10), PRIMARY KEY (Id))", (cmd, table) =>
        {
            var child = table + "_c";
            cmd.CommandText = $"CREATE TABLE `{child}` (ParentId INT NOT NULL, CONSTRAINT `FK_{child}` FOREIGN KEY (ParentId) REFERENCES `{table}` (Id))";
            cmd.ExecuteNonQuery();
            try
            {
                ClearAudit(cmd);
                RunTableQuenchProc(cmd, Json(table, "`Tenant`,`Id`"));
                Assert.Multiple(() =>
                {
                    Assert.That(PrimaryKey(cmd, table), Is.EqualTo("Id"), "the referenced key stays");
                    Assert.That(Audit(cmd, table), Does.Not.Contain("index"));
                });
                cmd.CommandText = $"INSERT INTO `{child}` (ParentId) VALUES (1)";
                Assert.That(() => cmd.ExecuteNonQuery(), Throws.Nothing, "and the foreign key still accepts a row whose parent exists");
            }
            finally
            {
                cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{child}`";
                cmd.ExecuteNonQuery();
            }
        });
    }
}
