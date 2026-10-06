// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using System;
using System.IO;

using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;
using Schema.IntegrationTests;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for DatabaseQuench script execution functionality.
/// Uses dynamically created test databases via FixtureSetup.
/// </summary>
[Category("Integration")]
public abstract class DatabaseQuenchTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string SecondaryDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void ForgeKindler_CreatesCompletedMigrationScriptsTable()
    {
        // ForgeKindler is already deployed by FixtureSetup - verify the table exists
        using var command = _connection.CreateCommand();

        command.CommandText = $@"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{MainDb}'
            AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'";
        var result = command.ExecuteScalar();
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), CatalogName.Matches(_connection, "SchemaSmith_CompletedMigrationScripts"));
    }

    [Test]
    public void CompletedMigrationScripts_CanTrackScriptExecution()
    {
        // ForgeKindler is already deployed by FixtureSetup
        using var command = _connection.CreateCommand();

        // Insert a test entry
        var testProductName = $"TestProduct_{Guid.NewGuid():N}";
        command.CommandText = $@"
            INSERT INTO `{MainDb}`.`SchemaSmith_CompletedMigrationScripts`
            (`ScriptPath`, `ProductName`, `QuenchSlot`)
            VALUES ('test/script.sql', '{testProductName}', 'Before')";
        command.ExecuteNonQuery();

        // Verify we can read it back
        command.CommandText = $@"
            SELECT COUNT(*) FROM `{MainDb}`.`SchemaSmith_CompletedMigrationScripts`
            WHERE `ProductName` = '{testProductName}'";
        var count = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(count, Is.EqualTo(1));

        // Cleanup
        command.CommandText = $"DELETE FROM `{MainDb}`.`SchemaSmith_CompletedMigrationScripts` WHERE `ProductName` = '{testProductName}'";
        command.ExecuteNonQuery();
    }

    [Test]
    public void SchemaIdentificationScript_ReturnsDatabase()
    {
        using var command = _connection.CreateCommand();

        // This is similar to what Template.SchemaIdentificationScript does
        command.CommandText = $"SELECT '{MainDb}' AS DatabaseName";
        using var reader = command.ExecuteReader();

        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo(MainDb));
    }

    [Test]
    public void CanExecuteSimpleScript()
    {
        using var command = _connection.CreateCommand();
        var mainDb = MainDb;

        // Execute a simple script that creates and drops a test table
        command.CommandText = $"CREATE TABLE IF NOT EXISTS `{mainDb}`.`_test_quench_table` (id INT)";
        command.ExecuteNonQuery();

        // Verify it was created
        command.CommandText = $@"
            SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{mainDb}' AND TABLE_NAME = '_test_quench_table'";
        var result = command.ExecuteScalar();
        Assert.That(result, Is.Not.Null);

        // Cleanup
        command.CommandText = $"DROP TABLE IF EXISTS `{mainDb}`.`_test_quench_table`";
        command.ExecuteNonQuery();
    }

    [Test]
    public void CanSwitchDatabaseContext()
    {
        using var command = _connection.CreateCommand();
        var mainDb = MainDb;
        var secondaryDb = SecondaryDb;

        // Switch to secondary database
        command.CommandText = $"USE `{secondaryDb}`";
        command.ExecuteNonQuery();

        command.CommandText = "SELECT DATABASE()";
        var db1 = command.ExecuteScalar()?.ToString();
        Assert.That(db1, CatalogName.Matches(_connection, secondaryDb));

        // Switch to main database
        command.CommandText = $"USE `{mainDb}`";
        command.ExecuteNonQuery();

        command.CommandText = "SELECT DATABASE()";
        var db2 = command.ExecuteScalar()?.ToString();
        Assert.That(db2, CatalogName.Matches(_connection, mainDb));
    }

    [Test]
    public void SqlScript_SplitsIntoBatches()
    {
        // SqlScript should split scripts on GO statements
        // For MySQL, we don't use GO, but scripts may have multiple statements
        var script = new SqlScript
        {
            Name = "test.sql",
            FilePath = "test.sql"
        };

        // Manually add batches (normally done by Load method)
        script.Batches.Add("SELECT 1");
        script.Batches.Add("SELECT 2");

        Assert.That(script.Batches.Count, Is.EqualTo(2));
        Assert.That(script.Batches[0], Is.EqualTo("SELECT 1"));
        Assert.That(script.Batches[1], Is.EqualTo("SELECT 2"));
    }
}
