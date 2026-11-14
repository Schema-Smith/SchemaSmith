// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class ParallelFanOutTests : ParallelFanOutSharedTests
{
    protected override string ProductPlatform => "MySQL";

    protected override IDbConnection OpenServer()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.ConnectionString + "Database=information_schema;");
        conn.Open();
        return conn;
    }

    protected override string CreateDatabaseSql(string db) => $"CREATE DATABASE `{db}`";

    protected override void DropDatabase(IDbCommand cmd, string db)
    {
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`";
        cmd.ExecuteNonQuery();
    }

    protected override string IdentificationScript(string prefix) =>
        $"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME LIKE '{prefix}\\_db%' ORDER BY SCHEMA_NAME";
    protected override string CurrentDatabaseQuery => "SELECT DATABASE()";
    protected override string ProbeFileName => "fanout_probe.sql";
    protected override string ProbeScript => "DROP FUNCTION IF EXISTS fanout_probe;\nCREATE FUNCTION fanout_probe() RETURNS VARCHAR(64) DETERMINISTIC RETURN '{{Who}}';";
    protected override string ProbeValueSql => "SELECT fanout_probe()";
    protected override string AfterScript => "CREATE TABLE IF NOT EXISTS after_marker (id INT NULL);";
    protected override string AfterMarkerExistsSql => "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'after_marker'";
}
