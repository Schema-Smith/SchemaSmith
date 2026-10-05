// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Declaring <c>EnableCDC</c> against a database where CDC is off at the DATABASE level must say so.
/// <para>It used to say nothing at all. The whole enable/disable pass sits inside
/// <c>IF EXISTS (… is_cdc_enabled = 1)</c> with no <c>ELSE</c>, so the deploy reported success, the table
/// was not tracked, and nothing in the output mentioned it. A user discovers that when someone asks
/// where the change history went.</para>
/// <para><b>This fixture runs in its own database with CDC deliberately NOT enabled.</b> The shared
/// SchemaQuench integration database calls <c>sys.sp_cdc_enable_db</c> in its fixture setup, so the
/// condition under test cannot exist there — a test written against it would pass while proving
/// nothing.</para>
/// <para>SchemaSmith deliberately does not turn the database toggle on. <c>sp_cdc_enable_db</c> changes
/// retention, cleanup jobs and storage for the whole database; enabling it because one table asked
/// would trade a silent no-op for a silent side effect on every other table in it.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class CdcDatabaseToggleTests
{
    private IDbConnection _connection;
    private string _db;

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[CdcWanted]",
            "EnableCDC": true,
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [ { "Name": "[PK_CdcWanted]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true } ]
        }]
        """;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaCdcToggle_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        _connection.ChangeDatabase(_db);

        using var cmd = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Guards the premise. If CDC were somehow on here, every assertion below would pass for the
        // wrong reason -- the enable path would simply work and nothing would be degraded.
        Assert.That(Scalar("SELECT CONVERT(INT, is_cdc_enabled) FROM sys.databases WHERE database_id = DB_ID()"),
            Is.Zero, "this fixture's database must NOT have CDC enabled, or it tests nothing");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            _connection.ChangeDatabase("master");
            // One batch, so a CDC or Change Tracking cleanup task cannot take the single-user slot before the DROP.
            Exec($"IF DB_ID('{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END");
        }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private int Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
    }

    private void RunQuench()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'CdcToggleTest', "
                          + $"@TableDefinitions = N'{TableJson.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void DeclaringEnableCdc_WithoutTheDatabaseToggle_IsRecordedAsDowngraded()
    {
        Exec("DELETE FROM SchemaSmith.ChangeAudit");

        RunQuench();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'CdcWanted'"), Is.EqualTo(1),
                "the table itself must still deploy -- the declaration that cannot be honoured is CDC, "
                + "not the table");

            Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                               + "WHERE ActionType = 'downgraded' AND ObjectName LIKE '%CdcWanted%'"),
                Is.EqualTo(1),
                "a declared feature that could not be applied must leave a 'downgraded' manifest row. "
                + "Without it the run is green, the table is untracked, and nothing anywhere says so -- "
                + "which is exactly the defect this test exists for.");

            Assert.That(Scalar("SELECT CONVERT(INT, is_tracked_by_cdc) FROM sys.tables WHERE name = 'CdcWanted'"),
                Is.Zero, "and CDC genuinely is not on, so the manifest row is telling the truth");
        });
    }

    [Test]
    public void ATableThatDoesNotAskForCdc_IsNotDowngraded()
    {
        // The negative half. Without it, a degrade that fired for every table would pass the test above.
        Exec("DELETE FROM SchemaSmith.ChangeAudit");

        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        const string plain = """
            [{
                "Schema": "[dbo]",
                "Name": "[NoCdcWanted]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "[PK_NoCdcWanted]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true } ]
            }]
            """;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'CdcToggleTest', "
                          + $"@TableDefinitions = N'{plain.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();

        Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                           + "WHERE ActionType = 'downgraded' AND ObjectName LIKE '%NoCdcWanted%'"),
            Is.Zero, "a table that never asked for CDC must not be reported as downgraded");
    }
}
