// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

using NUnit.Framework;

namespace DataTongs.IntegrationTests.SqlServer;

/// <summary>
/// End-to-end integration tests for DataTongs against SQL Server.
/// Tests the complete workflow: extract -> generate merge script -> apply -> verify.
/// </summary>
[TestFixture]
[Category("SqlServer")]
[Category("Integration")]
public class DataTongsEndToEndTests
{
    private string _integrationDb = "";
    private string _connectionString = "";
    private IDbConnection _connection = null!;
    private global::DataTongs.DataTongs _dataTongs = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("DTE2E");

        CreateTestDatabase();
    }

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        _connection.Open();
        _connection.ChangeDatabase(_integrationDb);
        _dataTongs = new global::DataTongs.DataTongs(Platform.SqlServer);
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
    }

    #region End-to-End Tests

    [Test]
    public void EndToEnd_ExtractAndReapply_DataMatches()
    {
        using var command = _connection.CreateCommand();
        var sourceTable = $"E2ESrc_{Guid.NewGuid():N}".Substring(0, 28);
        var targetTable = $"E2ETgt_{Guid.NewGuid():N}".Substring(0, 28);

        try
        {
            // Create source table with test data
            command.CommandText = $@"
CREATE TABLE [dbo].[{sourceTable}] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Code] VARCHAR(20) NOT NULL,
    [Name] NVARCHAR(100),
    [Amount] DECIMAL(10,2),
    [CreatedDate] DATE NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[{sourceTable}] ([Id], [Code], [Name], [Amount], [CreatedDate]) VALUES
    (1, 'A001', 'Item One', 100.50, '2024-01-15'),
    (2, 'A002', 'Item Two', 200.75, '2024-02-20'),
    (3, 'A003', 'Item Three', 300.00, '2024-03-25');
";
            command.ExecuteNonQuery();

            // Create empty target table with same structure
            command.CommandText = $@"
CREATE TABLE [dbo].[{targetTable}] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Code] VARCHAR(20) NOT NULL,
    [Name] NVARCHAR(100),
    [Amount] DECIMAL(10,2),
    [CreatedDate] DATE NOT NULL DEFAULT GETDATE()
);
";
            command.ExecuteNonQuery();

            // Extract data from source
            var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", sourceTable);
            var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, command, "dbo", sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", sourceTable, keyColumns, null);

            // Generate merge script for target table (Upsert: mergeUpdate=true, mergeDelete=false)
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", targetTable, json, keyColumns,
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // Execute script against target table
            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify data matches
            command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{targetTable}]";
            var targetCount = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(targetCount, Is.EqualTo(3));

            // Verify specific values
            command.CommandText = $"SELECT [Code], [Name], [Amount] FROM [dbo].[{targetTable}] WHERE [Id] = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("A001"));
            Assert.That(reader.GetString(1), Is.EqualTo("Item One"));
            Assert.That(reader.GetDecimal(2), Is.EqualTo(100.50m));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{sourceTable}]; DROP TABLE IF EXISTS [dbo].[{targetTable}];";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_PreservesDecimalPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"E2EDec_{Guid.NewGuid():N}".Substring(0, 28);

        try
        {
            // Create table with precise decimals
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Price] DECIMAL(10,4) NOT NULL,
    [Quantity] DECIMAL(15,6)
);

INSERT INTO [dbo].[{tableName}] VALUES
    (1, 123.4567, 9999.123456),
    (2, 0.0001, 0.000001);
";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", tableName, "[Id]", null);

            // Clear and re-insert via merge script (Insert: mergeUpdate=false, mergeDelete=false)
            command.CommandText = $"DELETE FROM [dbo].[{tableName}]";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, json, "[Id]",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify precision preserved
            command.CommandText = $"SELECT [Price], [Quantity] FROM [dbo].[{tableName}] WHERE [Id] = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetDecimal(0), Is.EqualTo(123.4567m));
            Assert.That(reader.GetDecimal(1), Is.EqualTo(9999.123456m));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    // A ROWGUIDCOL column was left out of extraction and the merge, so a delivered row got a fresh GUID from the column's
    // default: a silent data change for anything that keys on it. Both encodings carry it now.
    [TestCase("Json", TestName = "EndToEnd_RoundTrip_KeepsRowGuidColValues_Json")]
    [TestCase("Xml", TestName = "EndToEnd_RoundTrip_KeepsRowGuidColValues_Xml")]
    public void EndToEnd_RoundTrip_KeepsRowGuidColValues(string encoding)
    {
        using var command = _connection.CreateCommand();
        var tableName = $"E2EGuid_{Guid.NewGuid():N}".Substring(0, 28);
        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] ([Id] INT NOT NULL PRIMARY KEY, [G] UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL DEFAULT NEWID());
INSERT INTO [dbo].[{tableName}] VALUES (1, '11111111-2222-3333-4444-555555555555');";
            command.ExecuteNonQuery();

            var payload = encoding == "Xml"
                ? _dataTongs.GetTableDataXmlSqlServer(command, "dbo", tableName, "[Id]", null)
                : _dataTongs.GetTableDataJson(command, _dataTongs.GetSelectColumns(command, "dbo", tableName), "dbo", tableName, "[Id]", null);

            command.CommandText = $"DELETE FROM [dbo].[{tableName}]";
            command.ExecuteNonQuery();
            command.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, payload, "[Id]",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null,
                contentEncoding: encoding);
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CAST([G] AS NVARCHAR(36)) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("11111111-2222-3333-4444-555555555555").IgnoreCase);
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_HandlesNullValues()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"E2ENull_{Guid.NewGuid():N}".Substring(0, 28);

        try
        {
            // Create table with nullable columns
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT NOT NULL PRIMARY KEY,
    [OptionalText] NVARCHAR(100),
    [OptionalNumber] INT,
    [OptionalDate] DATE
);

INSERT INTO [dbo].[{tableName}] VALUES
    (1, 'has value', 42, '2024-01-01'),
    (2, NULL, NULL, NULL);
";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", tableName, "[Id]", null);

            // Clear and re-insert via merge script (Insert: mergeUpdate=false, mergeDelete=false)
            command.CommandText = $"DELETE FROM [dbo].[{tableName}]";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, json, "[Id]",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify nulls preserved
            command.CommandText = $"SELECT [OptionalText], [OptionalNumber] FROM [dbo].[{tableName}] WHERE [Id] = 2";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.IsDBNull(0), Is.True);
            Assert.That(reader.IsDBNull(1), Is.True);
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Helper Methods

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}];";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _integrationDb);
        conn.Close();
    }

    #endregion
}
