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
