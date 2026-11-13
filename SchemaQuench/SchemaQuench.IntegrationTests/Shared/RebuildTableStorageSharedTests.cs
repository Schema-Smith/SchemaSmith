// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

// A table rebuild created its replacement with only ENGINE and COLLATE. The table's other options -- row format,
// compression, statistics settings -- were reset unless the package declared them. The replacement is now
// created with the table's options, declared where the package declares them, as they were otherwise.
public abstract class RebuildTableStorageSharedTests : BaseTableQuenchTests
{
    protected sealed class Run : IDisposable
    {
        public readonly IDbConnection Conn;
        public readonly string Uid = Guid.NewGuid().ToString("N")[..8];
        public readonly string Db;
        public string Table => $"rbs_{Uid}";

        public Run(Platform platform, string connectionString, string db)
        {
            Db = db;
            Conn = DbConnectionFactory.ForPlatform(platform).GetDbConnection(connectionString);
            Conn.Open();
            Exec($"USE `{Db}`");
        }

        // Other fixtures' DDL runs alongside, and MariaDB refuses a catalog read that meets it (1684).
        public void Exec(string sql) => TransientRetry.Run(() =>
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        });

        public string? Scalar(string sql) => TransientRetry.Run(() =>
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? null : v.ToString();
        });

        public string Json(string valType, string extra = "") => $$"""
[ { "Name": "{{Table}}", "RebuildPolicy": { "Mode": "ALWAYS" }{{extra}},
    "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Val", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] } ]
""";

        public string Options() => Scalar(
            $"SELECT LOWER(CREATE_OPTIONS) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{Db}' AND TABLE_NAME = '{Table}'") ?? "";

        public string? Comment() => Scalar(
            $"SELECT TABLE_COMMENT FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{Db}' AND TABLE_NAME = '{Table}'");

        public string Rebuilt() => Scalar(
            $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE ObjectName = '{Table}' AND ActionType = 'rebuilt'") ?? "";

        public void Dispose()
        {
            Exec($"DROP TABLE IF EXISTS `{Table}`, `{Table}_SchemaSmithRebuild`, `{Table}_SchemaSmithOld`");
            Conn.Dispose();
        }
    }

    protected Run Seeded(string options = "")
    {
        var r = new Run(Platform, _connectionString, _mainDb);
        r.Exec($"CREATE TABLE `{r.Table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, PRIMARY KEY (`Id`)) {options}");
        r.Exec($"INSERT INTO `{r.Table}` VALUES (1, 'a'), (2, 'b')");
        r.Exec("SET SESSION group_concat_max_len = 1000000");
        return r;
    }

    protected void Deploy(Run r, string json) => RunTableQuenchProc(r.Conn.CreateCommand(), json);

    [Test]
    public void TheTablesOwnOptions_AreKept()
    {
        using var r = Seeded("ROW_FORMAT=COMPACT STATS_PERSISTENT=0 CHECKSUM=1");

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Rebuilt(), Is.EqualTo("1"), "setup: the table was rebuilt");
            Assert.That(r.Options(), Does.Contain("row_format=compact").And.Contain("stats_persistent=0").And.Contain("checksum=1"));
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM `{r.Table}`"), Is.EqualTo("2"));
        });
    }

    [Test]
    public void ADeclaredRowFormat_IsWrittenIntoTheReplacement()
    {
        using var r = Seeded("ROW_FORMAT=DYNAMIC STATS_PERSISTENT=0");
        var esc = r.Json("VARCHAR(60)", @", ""RowFormat"": ""COMPACT"", ""Comment"": ""declared""").Replace("'", "''");
        r.Exec($"CALL SchemaSmith_ParseTableJson('{r.Db}', '{esc}')");

        r.Exec($"CALL SchemaSmith_RebuildTable('{r.Db}', '{r.Table}', 0)");

        Assert.Multiple(() =>
        {
            Assert.That(r.Options(), Does.Contain("row_format=compact").And.Contain("stats_persistent=0").And.Not.Contain("dynamic"),
                "the declared format, with the undeclared options the table had");
            Assert.That(r.Comment(), Is.EqualTo("declared"));
        });
    }
}
