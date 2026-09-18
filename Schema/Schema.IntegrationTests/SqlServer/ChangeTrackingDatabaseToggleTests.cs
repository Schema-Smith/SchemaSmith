// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Declaring <c>EnableChangeTracking</c> against a database where Change Tracking is off at the DATABASE
/// level must say so, rather than deploying green and leaving the table untracked.
/// <para>This is the same shape as <see cref="CdcDatabaseToggleTests"/>, and deliberately so: CDC shipped
/// that silent no-op for releases, and the rule this fixture pins is that the second feature to depend on
/// a database-scoped toggle does not repeat it.</para>
/// <para>SchemaSmith does not turn the toggle on. <c>ALTER DATABASE … SET CHANGE_TRACKING = ON</c> sets
/// retention and auto-cleanup for the whole database; enabling it because one table asked would trade a
/// silent no-op for a silent database-wide side effect.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ChangeTrackingDatabaseToggleTests
{
    private IDbConnection _connection;
    private string _db;

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[CtWantedNoDb]",
            "EnableChangeTracking": true,
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [ { "Name": "[PK_CtWantedNoDb]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true } ]
        }]
        """;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaCtOff_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        _connection.ChangeDatabase(_db);

        using var cmd = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        Assert.That(Scalar("SELECT COUNT(*) FROM sys.change_tracking_databases WHERE database_id = DB_ID()"),
            Is.Zero, "this fixture's database must NOT have Change Tracking enabled, or it tests nothing");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            _connection.ChangeDatabase("master");
            Exec($"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Exec($"DROP DATABASE IF EXISTS [{_db}]");
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

    [Test]
    public void DeclaringChangeTracking_WithoutTheDatabaseToggle_IsRecordedAsDowngraded()
    {
        Exec("DELETE FROM SchemaSmith.ChangeAudit");

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'CtToggleTest', "
                              + $"@TableDefinitions = N'{TableJson.Replace("'", "''")}'";
            cmd.ExecuteNonQuery();
        }

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'CtWantedNoDb'"), Is.EqualTo(1),
                "the table still deploys -- what cannot be honoured is Change Tracking, not the table");

            Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                               + "WHERE ActionType = 'downgraded' AND ObjectName LIKE '%CtWantedNoDb%'"),
                Is.EqualTo(1),
                "a declared feature that could not be applied must leave a 'downgraded' manifest row -- the "
                + "downstream tooling drives behaviour off that manifest and cannot see a degrade that writes nothing");

            Assert.That(Scalar("SELECT COUNT(*) FROM sys.change_tracking_tables "
                               + "WHERE [object_id] = OBJECT_ID('dbo.CtWantedNoDb')"),
                Is.Zero, "and tracking genuinely is not on, so the manifest row is telling the truth");
        });
    }

    [Test]
    public void ATableThatDoesNotAskForChangeTracking_IsNotDowngraded()
    {
        Exec("DELETE FROM SchemaSmith.ChangeAudit");

        const string plain = """
            [{
                "Schema": "[dbo]",
                "Name": "[NoCtWanted]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "[PK_NoCtWanted]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true } ]
            }]
            """;
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'CtToggleTest', "
                          + $"@TableDefinitions = N'{plain.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();

        Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                           + "WHERE ActionType = 'downgraded' AND ObjectName LIKE '%NoCtWanted%'"),
            Is.Zero, "a table that never asked for Change Tracking must not be reported as downgraded");
    }
}
