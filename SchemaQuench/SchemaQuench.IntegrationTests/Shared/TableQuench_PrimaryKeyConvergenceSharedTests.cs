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

    // A new table's CREATE carried only its columns and primary key, so an AUTO_INCREMENT column keyed only by another
    // declared index failed with 1075, though the same table extracts fine. The declared index now goes in the CREATE;
    // with DropUnknownIndexes on, the redeploy finds nothing undeclared to drop.
    [TestCase(null, "UNIQUE", TestName = "AnAutoIncrementKeyedOnlyByAUniqueIndex_IsCreated")]
    [TestCase("`Tenant`,`Id`", "", TestName = "AnAutoIncrementKeyedBySecondaryIndexBehindTheKey_IsCreated")]
    [TestCase("`Tenant`", "UNIQUE", TestName = "AnAutoIncrementOutsideTheKey_KeyedByAUniqueIndex_IsCreated")]
    public void ANewTableWhoseAutoIncrementIsKeyedByADeclaredIndex_IsCreated_AndRedeploysClean(string primaryKey, string unique)
    {
        var table = $"PkAutoNew_{Guid.NewGuid().ToString("N")[..8]}";
        var json = $$"""
            [{
                "Name": "{{table}}",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false, "AutoIncrement": true },
                    { "Name": "Tenant", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    {{(primaryKey == null ? "" : $$"""{ "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{primaryKey}}" },""")}}
                    { "Name": "ix_{{table}}_id", "Unique": {{(unique == "UNIQUE" ? "true" : "false")}}, "IndexColumns": "`Id`" }
                ]
            }]
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            string Indexes()
            {
                cmd.CommandText = $@"SELECT GROUP_CONCAT(DISTINCT INDEX_NAME ORDER BY INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS
                                      WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'";
                return TransientRetry.Run(() => cmd.ExecuteScalar())?.ToString() ?? "";
            }
            var expected = primaryKey == null ? $"ix_{table}_id" : $"ix_{table}_id,PRIMARY";

            RunTableQuenchProc(cmd, json);
            Assert.That(Indexes(), Is.EqualTo(expected), "the CREATE carries the declared index, not an unnamed key of its own");

            cmd.CommandText = $"CALL SchemaSmith_TableQuench('{_productName}', '{_mainDb}', '{json.Replace("'", "''")}', 0, 1, 0)";
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
            Assert.That(Indexes(), Is.EqualTo(expected), "and DropUnknownIndexes finds nothing undeclared");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    // An omitted Engine was read as InnoDB, so ALTER TABLE ... ENGINE = InnoDB converted an existing MyISAM, Aria or MEMORY
    // table nobody asked to convert. Omitted leaves it; a declared engine still converges.
    [Test]
    public void AnOmittedEngine_LeavesAnExistingTablesEngine_AndADeclaredOneConverges()
    {
        WithTable("EngineKept", "(Id INT NOT NULL, Tenant INT NOT NULL, Val VARCHAR(10), PRIMARY KEY (Id)) ENGINE=MyISAM", (cmd, table) =>
        {
            string Engine()
            {
                cmd.CommandText = $"SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'";
                return cmd.ExecuteScalar()?.ToString() ?? "";
            }
            RunTableQuenchProc(cmd, Json(table, "`Id`"));
            Assert.That(Engine(), Is.EqualTo("MyISAM"), "an omitted Engine leaves the table's own");

            RunTableQuenchProc(cmd, Json(table, "`Id`").Replace($"\"Name\": \"{table}\",", $"\"Name\": \"{table}\", \"Engine\": \"InnoDB\","));
            Assert.That(Engine(), Is.EqualTo("InnoDB"), "a declared engine is applied");
        });
    }

    // B14: a foreign key's columns written with spaces after the commas ("`A`, `B`") never matched the catalog's list,
    // so the key and its same-named index were dropped and re-created on every deploy.
    [Test]
    public void AForeignKeyWrittenWithSpacedColumns_RedeploysAsANoOp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"FkSpParent_{suffix}";
        var child = $"FkSpChild_{suffix}";
        var json = $$"""
            [{
                "Name": "{{parent}}",
                "Columns": [ { "Name": "A", "DataType": "INT", "Nullable": false }, { "Name": "B", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`A`,`B`" } ]
            },
            {
                "Name": "{{child}}",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "A", "DataType": "INT", "Nullable": false }, { "Name": "B", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ],
                "ForeignKeys": [ { "Name": "fk_{{suffix}}", "Columns": "`A`, `B`", "RelatedTable": "{{parent}}", "RelatedColumns": "`A`, `B`" } ]
            }]
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, json);
            Assert.That(Audit(cmd, child), Does.Not.Contain("foreignKey"), "the unchanged key is left as it is");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{child}`; DROP TABLE IF EXISTS `{_mainDb}`.`{parent}`";
            cmd.ExecuteNonQuery();
        }
    }

    // A7: which generated column reads which was a substring test, so `total` reading `tax_amount` was taken to read
    // `tax`, which reads `total`, and the deploy stopped on a cycle that does not exist.
    [Test]
    public void GeneratedColumnsWhoseNamesContainEachOther_AreNotACycle()
    {
        var table = $"GenCycle_{Guid.NewGuid().ToString("N")[..8]}";
        var json = $$"""
            [{
                "Name": "{{table}}",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "price", "DataType": "DECIMAL(10,2)", "Nullable": true },
                    { "Name": "tax_amount", "DataType": "DECIMAL(10,2)", "Nullable": true },
                    { "Name": "total", "DataType": "DECIMAL(10,2)", "Nullable": true, "GenerationExpression": "price + tax_amount", "Generated": "STORED" },
                    { "Name": "tax", "DataType": "DECIMAL(10,2)", "Nullable": true, "GenerationExpression": "total * 0.2", "Generated": "STORED" }
                ],
                "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ]
            }]
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json));
            cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND COLUMN_NAME IN ('total', 'tax')";
            Assert.That(Convert.ToInt32(TransientRetry.Run(() => cmd.ExecuteScalar())), Is.EqualTo(2));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
        }
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
