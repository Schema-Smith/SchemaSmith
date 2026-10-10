// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

using NUnit.Framework;

namespace DataTongs.IntegrationTests.SqlServer;

/// <summary>
/// Integration tests for DataTongs merge script output against SQL Server.
/// Tests MERGE statement generation for upsert, insert-only, and full sync modes.
/// </summary>
[TestFixture]
[Category("SqlServer")]
[Category("Integration")]
public class DataTongsOutputTests
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
        _integrationDb = GenerateUniqueDBName("DTOutput");

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

    #region Merge Script Output Tests

    [Test]
    public void MergeScript_Upsert_GeneratesMergeStatement()
    {
        using var command = _connection.CreateCommand();

        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Country");
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, command, "dbo", "Country");
        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Country", keyColumns, "CountryId <= 3");

        // Upsert: mergeUpdate=true, mergeDelete=false
        var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", "Country", json, keyColumns,
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(script, Does.Contain("MERGE INTO [dbo].[Country]"));
        Assert.That(script, Does.Contain("DECLARE @v_json NVARCHAR(MAX)"));
        Assert.That(script, Does.Contain("WHEN MATCHED AND"));
        Assert.That(script, Does.Contain("THEN"));
        Assert.That(script, Does.Contain("WHEN NOT MATCHED"));
        Assert.That(script, Does.Not.Contain("WHEN NOT MATCHED BY SOURCE"));
    }

    [Test]
    public void MergeScript_Insert_GeneratesMergeWithoutUpdate()
    {
        using var command = _connection.CreateCommand();

        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Country");
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, command, "dbo", "Country");
        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Country", keyColumns, "CountryId = 1");

        // Insert only: mergeUpdate=false, mergeDelete=false
        var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", "Country", json, keyColumns,
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(script, Does.Contain("MERGE INTO [dbo].[Country]"));
        Assert.That(script, Does.Contain("DECLARE @v_json NVARCHAR(MAX)"));
        Assert.That(script, Does.Contain("WHEN NOT MATCHED"));
        Assert.That(script, Does.Not.Contain("WHEN MATCHED THEN"));
    }

    [Test]
    public void MergeScript_Replace_GeneratesMergeWithDelete()
    {
        using var command = _connection.CreateCommand();

        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Country");
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, command, "dbo", "Country");
        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Country", keyColumns, "CountryId <= 3");

        // Full sync: mergeUpdate=true, mergeDelete=true
        var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", "Country", json, keyColumns,
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(script, Does.Contain("MERGE INTO [dbo].[Country]"));
        Assert.That(script, Does.Contain("WHEN NOT MATCHED BY SOURCE"));
    }

    [Test]
    public void GeneratedScript_CanBeExecuted()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"TestExec_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create test table
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Code] VARCHAR(10) NOT NULL,
    [Name] NVARCHAR(100),
    [Value] DECIMAL(10,2),
    CONSTRAINT [UQ_{tableName}_Code] UNIQUE ([Code])
);

INSERT INTO [dbo].[{tableName}] ([Id], [Code], [Name], [Value]) VALUES (1, 'A01', 'Initial', 10.00);
";
            command.ExecuteNonQuery();

            // Generate merge script (Upsert: mergeUpdate=true, mergeDelete=false)
            var tableData = @"[{""Code"":""A01"",""Id"":1,""Name"":""Updated"",""Value"":20.00},{""Code"":""A02"",""Id"":2,""Name"":""New"",""Value"":30.00}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // Execute the script as a single batch (SQL Server MERGE is one statement)
            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify results
            command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{tableName}]";
            var count = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(count, Is.EqualTo(2)); // 1 updated + 1 new

            command.CommandText = $"SELECT [Name] FROM [dbo].[{tableName}] WHERE [Code] = 'A01'";
            var name = command.ExecuteScalar()?.ToString();
            Assert.That(name, Is.EqualTo("Updated"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void GeneratedScript_Insert_SkipsExisting()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"TestIns_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create test table
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(100)
);

INSERT INTO [dbo].[{tableName}] VALUES (1, 'Original');
";
            command.ExecuteNonQuery();

            // Generate insert-only script (mergeUpdate=false, mergeDelete=false)
            var tableData = @"[{""Id"":1,""Name"":""Ignored""},{""Id"":2,""Name"":""New""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, tableData, "[Id]",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // Execute
            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify - id=1 should still be "Original", id=2 should be "New"
            command.CommandText = $"SELECT [Name] FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var name1 = command.ExecuteScalar()?.ToString();
            Assert.That(name1, Is.EqualTo("Original")); // Not replaced

            command.CommandText = $"SELECT [Name] FROM [dbo].[{tableName}] WHERE [Id] = 2";
            var name2 = command.ExecuteScalar()?.ToString();
            Assert.That(name2, Is.EqualTo("New")); // Inserted
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

        conn.ChangeDatabase(_integrationDb);

        cmd.CommandText = @"
CREATE TABLE [dbo].[Country] (
    [CountryId] INT NOT NULL PRIMARY KEY,
    [CountryName] NVARCHAR(100) NOT NULL,
    [LastUpdate] DATETIME2 NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[Country] ([CountryId], [CountryName]) VALUES
    (1, 'United States'),
    (2, 'Canada'),
    (3, 'Mexico');
";
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
