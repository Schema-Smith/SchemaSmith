// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// MY-027: a foreign key re-pointed at a same-named table in another database compared equal (the snapshot held only the
/// referenced table's name), so it kept referencing the old one at exit 0. The second database is the thing under test.
/// </summary>
public abstract class TableQuench_ForeignKeySchemaSharedTests : BaseTableQuenchTests
{
    // MA-054: widening a key pair (INT -> BIGINT on both sides) was refused, "Cannot change column ...: used in a foreign
    // key constraint", on every MariaDB version probed and on MySQL: the type change ran with the key still in place.
    [Test]
    public void AKeyPairWidenedOnBothSides_Deploys_AndTheForeignKeyIsBack()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(string type) => $$"""
            [{ "Name": "wpar_{{uid}}", "Columns": [ { "Name": "Id", "DataType": "{{type}}", "Nullable": false } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] },
             { "Name": "wchd_{{uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "ParentId", "DataType": "{{type}}", "Nullable": true } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
               "ForeignKeys": [ { "Name": "fkw_{{uid}}", "Columns": "ParentId", "RelatedTable": "wpar_{{uid}}", "RelatedColumns": "Id" } ] }]
            """;
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        try
        {
            RunTableQuenchProc(cmd, Json("INT"));
            RunTableQuenchProc(cmd, Json("BIGINT"));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar($"SELECT GROUP_CONCAT(DATA_TYPE ORDER BY TABLE_NAME) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND ((TABLE_NAME = 'wpar_{uid}' AND COLUMN_NAME = 'Id') OR (TABLE_NAME = 'wchd_{uid}' AND COLUMN_NAME = 'ParentId'))"),
                    Is.EqualTo("bigint,bigint"), "both sides widened");
                Assert.That(Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fkw_{uid}'"),
                    Is.EqualTo("1"), "and the foreign key is back");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `wchd_{uid}`; DROP TABLE IF EXISTS `wpar_{uid}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{uid}%'";
            cmd.ExecuteNonQuery();
        }
    }

    // MA-055 (MDEV-35630): MariaDB 10.4 to 11.4.12 refused to widen a column that follows a STORED generated column
    // while the table had a foreign key on any column, naming that unrelated key (1832). The floor sweep's 10.6 band runs it.
    [Test]
    public void AColumnAfterAStoredGeneratedColumnWidened_OnATableWithAnUnrelatedForeignKey_Deploys_AndTheKeyIsBack()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(int width) => $$"""
            [{ "Name": "gpar_{{uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] },
             { "Name": "gchd_{{uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false },
                 { "Name": "Flag", "DataType": "INT", "Nullable": true },
                 { "Name": "Twice", "DataType": "INT", "Nullable": true, "GenerationExpression": "`Flag` * 2", "Generated": "STORED" },
                 { "Name": "ParentId", "DataType": "INT", "Nullable": true },
                 { "Name": "Label", "DataType": "VARCHAR({{width}})", "Nullable": true } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
               "ForeignKeys": [ { "Name": "fkg_{{uid}}", "Columns": "ParentId", "RelatedTable": "gpar_{{uid}}", "RelatedColumns": "Id" } ] }]
            """;
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        try
        {
            // Built by hand: a deploy creates generated columns last, so only a table it did not create has one mid-row.
            cmd.CommandText = $"""
                CREATE TABLE gpar_{uid} (Id INT NOT NULL PRIMARY KEY);
                CREATE TABLE gchd_{uid} (Id INT NOT NULL PRIMARY KEY, Flag INT NULL, Twice INT AS (Flag * 2) STORED,
                  ParentId INT NULL, Label VARCHAR(10) NULL, KEY fkg_{uid} (ParentId),
                  CONSTRAINT fkg_{uid} FOREIGN KEY (ParentId) REFERENCES gpar_{uid} (Id))
                """;
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(25));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar($"SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'gchd_{uid}' AND COLUMN_NAME = 'Label'"),
                    Is.EqualTo("varchar(25)"), "widened");
                Assert.That(Scalar($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fkg_{uid}'"),
                    Is.EqualTo("1"), "and the foreign key is back");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `gchd_{uid}`; DROP TABLE IF EXISTS `gpar_{uid}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{uid}%'";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AForeignKeyMovedToAnotherDatabasesTable_ReferencesTheDeclaredOne()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var other = $"fkother_{uid}";
        var child = $"fkchild_{uid}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Exec(string sql)
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        string Json(string schema) => $$"""
            [{ "Name": "{{child}}",
               "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "ParentId", "DataType": "INT", "Nullable": true } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
               "ForeignKeys": [ { "Name": "fk_{{uid}}", "Columns": "ParentId", {{schema}}"RelatedTable": "fkparent_{{uid}}", "RelatedColumns": "Id" } ] }]
            """;
        string Referenced()
        {
            cmd.CommandText = $"SELECT CONCAT(UNIQUE_CONSTRAINT_SCHEMA, '.', REFERENCED_TABLE_NAME) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fk_{uid}'";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        try
        {
            Exec($"CREATE DATABASE `{other}`; CREATE TABLE `{other}`.`fkparent_{uid}` (Id INT NOT NULL PRIMARY KEY); CREATE TABLE `{_mainDb}`.`fkparent_{uid}` (Id INT NOT NULL PRIMARY KEY)");
            RunTableQuenchProc(cmd, Json(""));
            Assert.That(Referenced(), Is.EqualTo($"{_mainDb}.fkparent_{uid}").IgnoreCase, "precondition: its own database");

            RunTableQuenchProc(cmd, Json($"\"RelatedTableSchema\": \"{other}\", "));
            Assert.That(Referenced(), Is.EqualTo($"{other}.fkparent_{uid}").IgnoreCase, "re-pointed at the other database's table");

            Exec("DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()");
            RunTableQuenchProc(cmd, Json($"\"RelatedTableSchema\": \"{other}\", "));
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%fk_{uid}%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "then a redeploy is a no-op");
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{_mainDb}`.`{child}`; DROP TABLE IF EXISTS `{_mainDb}`.`fkparent_{uid}`; DROP DATABASE IF EXISTS `{other}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '%{uid}%'");
        }
    }
}
