// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain;
using Schema.Settings;

namespace Schema.UnitTests.Settings;

// Extraction's script output: one file per setting a package can run on every deploy, so each has to be safe to repeat.
[TestFixture]
public class SettingScriptTests
{
    [Test]
    public void SqlServerConfiguration_ComparesThenSets()
    {
        var sql = new SqlServerConfigurationDescriptor().RenderScript("cost threshold for parallelism", "50", "db");

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("IF EXISTS (SELECT 1 FROM sys.configurations WHERE name = N'cost threshold for parallelism' AND CAST(value AS BIGINT) <> 50)"));
            Assert.That(sql, Does.Contain("EXEC sp_configure N'cost threshold for parallelism', 50; RECONFIGURE;"));
            Assert.That(sql, Does.Contain("EXEC sp_configure 'show advanced options', 0; RECONFIGURE;"), "advanced options put back as they were");
        });
    }

    [Test]
    public void SqlServerOption_ComparesAgainstEverySpellingTheCatalogUses()
    {
        var sql = new SqlServerOptionDescriptor().RenderScript("AUTO_CLOSE", "OFF", "db");

        Assert.That(sql, Is.EqualTo(
            "IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = DB_NAME() AND CAST(is_auto_close_on AS NVARCHAR(128)) IN (N'OFF', N'0'))\r\n" +
            "BEGIN\r\n" +
            "    DECLARE @sql NVARCHAR(MAX) = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' SET AUTO_CLOSE OFF';\r\n" +
            "    EXEC (@sql);\r\n" +
            "END\r\n"), "ALTER DATABASE CURRENT is 2012 and later; the floor is 2008");
    }

    [Test]
    public void SqlServerScopedConfiguration_ComparesThenSets()
    {
        var sql = new SqlServerScopedConfigurationDescriptor(16).RenderScript("MAXDOP", "4", "db");

        Assert.That(sql, Is.EqualTo(
            "IF NOT EXISTS (SELECT 1 FROM sys.database_scoped_configurations WHERE UPPER(name) = N'MAXDOP' AND CAST(value AS NVARCHAR(4000)) IN (N'4'))\r\n" +
            "    ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 4;\r\n"));
    }

    [Test]
    public void SqlServerTraceFlag_SaysItDoesNotSurviveARestart()
    {
        var sql = new SqlServerTraceFlagDescriptor().RenderScript("1117", "ON", "db");
        Assert.That(sql, Does.Contain("DBCC TRACEON(1117, -1);").And.Contain("-T1117"));
    }

    [Test]
    public void PostgreSqlDatabaseParameter_SetsTheCurrentDatabase()
    {
        var sql = new PostgreSqlDatabaseParameterDescriptor().RenderScript("work_mem", "8MB", "db");
        Assert.That(sql, Is.EqualTo("DO $$ BEGIN EXECUTE format('ALTER DATABASE %I SET \"work_mem\" = %L', current_database(), '8MB'); END $$;\r\n"));
    }

    [Test]
    public void PostgreSqlServerParameter_AltersTheSystemAndReloads()
    {
        var sql = new PostgreSqlServerParameterDescriptor().RenderScript("log_min_duration_statement", "250", null);
        Assert.That(sql, Is.EqualTo("ALTER SYSTEM SET \"log_min_duration_statement\" = '250';\r\nSELECT pg_reload_conf();\r\n"));
    }

    [TestCase(Platform.MySQL, 8, "SET PERSIST max_connections = 180;\r\n")]
    [TestCase(Platform.MariaDb, 11, "SET GLOBAL max_connections = 180;\r\n")]
    public void MySqlVariable_PersistsWhereTheServerCan(Platform platform, int major, string expected)
    {
        Assert.That(new MySqlVariableDescriptor(platform, major).RenderScript("max_connections", "180", null), Is.EqualTo(expected));
    }

    [Test]
    public void MySqlOption_SetsTheDefaultDatabase()
    {
        Assert.That(new MySqlOptionDescriptor().RenderScript("COLLATE", "utf8mb4_bin", "db"), Is.EqualTo("ALTER DATABASE COLLATE = 'utf8mb4_bin';\r\n"));
    }

    [Test]
    public void AValueThatIsNotAToken_IsRefusedInAScriptToo()
    {
        Assert.That(() => new SqlServerOptionDescriptor().RenderScript("AUTO_CLOSE", "OFF; DROP", "db"), Throws.InstanceOf<SettingValueException>());
    }
}
