// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB refuses a UNIQUE index or constraint on a system-versioned table unless
/// <c>system_versioning_alter_history = KEEP</c> (4119), so a versioned table with a unique key could not be deployed
/// fresh (MA-070). A table created in this run has no history to protect, so the index pass sets <c>KEEP</c> around its
/// index work and puts the session back afterwards. An existing versioned table keeps the documented contract:
/// SchemaSmith sets <c>KEEP</c> only when the environment asks, and otherwise the refusal names the setting to change.
/// </summary>
[Category("MariaDb")]
[TestFixture]
public class VersionedTableIndexTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDbName => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();

    private static string Json(string table, bool withUnique) => $$"""
        [{
            "Name": "{{table}}",
            "IsSystemVersioned": true,
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": true },
                { "Name": "Ref", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true },
                { "Name": "ix_ref", "IndexColumns": "Ref" }{{(withUnique ? ",\n{ \"Name\": \"uq_code\", \"IndexColumns\": \"Code\", \"Unique\": true }" : "")}}
            ]
        }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        if (!FixtureSetup.SupportsSystemVersioning(Scalar(cmd, "SELECT VERSION()")))
            Assert.Ignore("System-versioned tables are MariaDB 10.3+.");
        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`; SET @ss_system_versioning_alter_history = NULL";
        cmd.ExecuteNonQuery();
        try
        {
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"SET @ss_system_versioning_alter_history = NULL; DROP TABLE IF EXISTS `{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ANewVersionedTable_WithAUniqueIndex_DeploysFresh_AndTheSessionIsPutBack()
    {
        const string table = "VersionedUniqueFresh";
        WithTable(table, cmd =>
        {
            Assert.That(Scalar(cmd, "SELECT @@system_versioning_alter_history"), Is.EqualTo("ERROR"), "setup: the session default");
            RunTableQuenchProc(cmd, Json(table, withUnique: true));
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = 'uq_code' AND NON_UNIQUE = 0"),
                    Is.EqualTo("1"), "the unique index exists");
                Assert.That(Scalar(cmd, $"SELECT TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'"),
                    Is.EqualTo("SYSTEM VERSIONED"));
                Assert.That(Scalar(cmd, "SELECT @@system_versioning_alter_history"), Is.EqualTo("ERROR"), "KEEP did not outlive the new table's index work");
            });
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, withUnique: true)), "and a redeploy is clean");
        });
    }

    [Test]
    public void AnExistingVersionedTable_GainingAUniqueIndex_NamesTheSettingToChange_AndDeploysWithIt()
    {
        const string table = "VersionedUniqueLater";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, withUnique: false));
            cmd.CommandText = $"INSERT INTO `{table}` (Id, Code, Ref) VALUES (1, 'a', 1); UPDATE `{table}` SET Ref = 2";
            cmd.ExecuteNonQuery();

            string refusal = null;
            try { RunTableQuenchProc(cmd, Json(table, withUnique: true)); }
            catch (MySqlConnector.MySqlException e) { refusal = e.Message; }
            Assert.That(refusal, Is.Not.Null, "history exists and KEEP was not asked for, so the engine refuses the key");
            Assert.That(refusal, Does.Contain(table).And.Contain("SystemVersioningAlterHistory"), "the refusal names the table and the setting");

            cmd.CommandText = "SET @ss_system_versioning_alter_history = 'KEEP'";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table, withUnique: true));
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = 'uq_code'"),
                Is.EqualTo("1"), "with the environment's KEEP the index is created");
        });
    }
}
