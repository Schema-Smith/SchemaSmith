// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

// MySQL 8.0.30+ with sql_generate_invisible_primary_key on gives a table created without a primary key an invisible
// my_row_id key of its own. The server made it, not the package: extraction leaves it out, a redeploy does not drop it,
// and a package that declares a primary key replaces it. The deploy runs on this connection, so a session setting
// stands in for the server one.
[Category("MySQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_GeneratedInvisiblePrimaryKeyTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    private static string Json(string table, bool primaryKey) => $$"""
        [{
            "Name": "{{table}}",
            "DropColumnsRemovedFromProduct": true,
            "DropIndexesRemovedFromProduct": true,
            "Columns": [
                { "Name": "a", "DataType": "INT", "Nullable": false },
                { "Name": "b", "DataType": "VARCHAR(10)", "Nullable": true }
            ],
            "Indexes": [{{(primaryKey ? """{ "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`a`" }""" : "")}}]
        }]
        """;

    private void WithGeneratedKeys(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM (SELECT @@sql_generate_invisible_primary_key) v";
        try { cmd.ExecuteScalar(); }
        catch (System.Data.Common.DbException) { Assert.Ignore("Generated invisible primary keys arrived in MySQL 8.0.30."); }
        cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SET SESSION sql_generate_invisible_primary_key = ON";
        cmd.ExecuteNonQuery();
        try
        {
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"SET SESSION sql_generate_invisible_primary_key = OFF; DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    private string Columns(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private string PrimaryKey(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'PRIMARY'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    [Test]
    public void AKeylessTable_KeepsTheKeyTheServerGaveIt_AndARedeployDropsNothing()
    {
        const string table = "GipkKeep";
        WithGeneratedKeys(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, primaryKey: false));
            Assert.That(PrimaryKey(cmd, table), Is.EqualTo("my_row_id"), "the server gives the keyless table its key");

            RunTableQuenchProc(cmd, Json(table, primaryKey: false));
            Assert.Multiple(() =>
            {
                Assert.That(Columns(cmd, table), Is.EqualTo("my_row_id,a,b"), "a redeploy does not drop the generated column");
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("my_row_id"), "or its key");
            });
        });
    }

    [Test]
    public void Extraction_LeavesOutTheGeneratedKey()
    {
        const string table = "GipkExtract";
        WithGeneratedKeys(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, primaryKey: false));
            cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{table}')";
            var json = new StringBuilder();
            using (var reader = TransientRetry.Run(() => cmd.ExecuteReader()))
                while (reader.Read()) json.Append(reader.GetString(0));
            var extracted = JObject.Parse(json.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(extracted["Columns"]?.Select(c => (string)c["Name"]), Is.EqualTo(new[] { "`a`", "`b`" }), json.ToString());
                Assert.That(extracted["Indexes"]?.Select(i => (string)i["Name"]) ?? [], Is.Empty, json.ToString());
            });
        });
    }

    [Test]
    public void APackageThatDeclaresAPrimaryKey_ReplacesTheGeneratedOne()
    {
        const string table = "GipkReplace";
        WithGeneratedKeys(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, primaryKey: false));
            RunTableQuenchProc(cmd, Json(table, primaryKey: true));
            Assert.Multiple(() =>
            {
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("a"), "the declared key is the table's key");
                Assert.That(Columns(cmd, table), Is.EqualTo("a,b"), "and the generated column is gone");
            });
        });
    }

    [Test]
    public void AHiddenGeneratedKey_IsReplacedTooWhenAPrimaryKeyIsDeclared()
    {
        const string table = "GipkHidden";
        WithGeneratedKeys(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, primaryKey: false));
            cmd.CommandText = "SET SESSION show_gipk_in_create_table_and_information_schema = OFF";
            cmd.ExecuteNonQuery();
            try
            {
                RunTableQuenchProc(cmd, Json(table, primaryKey: true));
            }
            finally
            {
                cmd.CommandText = "SET SESSION show_gipk_in_create_table_and_information_schema = ON";
                cmd.ExecuteNonQuery();
            }
            Assert.Multiple(() =>
            {
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("a"), "the catalog showed no key, and the declared one replaced the hidden one");
                Assert.That(Columns(cmd, table), Is.EqualTo("a,b"));
            });
        });
    }

    [Test]
    public void ATableCreatedWithItsPrimaryKey_GetsNoGeneratedOne()
    {
        const string table = "GipkDeclared";
        WithGeneratedKeys(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, primaryKey: true));
            Assert.Multiple(() =>
            {
                Assert.That(PrimaryKey(cmd, table), Is.EqualTo("a"));
                Assert.That(Columns(cmd, table), Is.EqualTo("a,b"));
            });
        });
    }
}
