// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;
using Newtonsoft.Json;

namespace SchemaTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class GenerateIndexedViewJsonTests
{
    private string _integrationDb = "";
    private string _connectionString;
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("GenIvJson");

        CreateTestDatabases();

        _testConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], _integrationDb, config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
    }

    [Test]
    public void ShouldGenerateCorrectJsonForIndexedView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
EXEC('CREATE SCHEMA [Test]')
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TABLE Test.SourceTable (Id INT NOT NULL, Name VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_SourceTable ON Test.SourceTable (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE VIEW Test.vTestSummary WITH SCHEMABINDING AS
SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount
FROM Test.SourceTable
GROUP BY Id, Name;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE UNIQUE CLUSTERED INDEX [IX_vTestSummary_Id] ON Test.vTestSummary (Id);
CREATE NONCLUSTERED INDEX [IX_vTestSummary_Name] ON Test.vTestSummary (Name);
";
        cmd.ExecuteNonQuery();

        // Install the extraction function (via KindleOneFile so the GO-batched idempotent create is split).
        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateIndexedViewJson.sql", Platform.SqlServer);

        // Call it
        cmd.CommandText = "SELECT [SchemaSmith].[GenerateIndexedViewJson]('Test', 'vTestSummary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<SqlServerIndexedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo("[Test]"));
        Assert.That(view.Name, Is.EqualTo("[vTestSummary]"));
        Assert.That(view.Definition, Does.Contain("Id"));
        Assert.That(view.Definition, Does.Contain("Name"));
        // Definition should be SELECT only, not contain CREATE VIEW
        Assert.That(view.Definition, Does.Not.Contain("CREATE VIEW"));
        Assert.That(view.Indexes, Has.Count.EqualTo(2));

        // Clustered index should come first
        var clustered = view.Indexes[0];
        Assert.That(clustered.Name, Is.EqualTo("[IX_vTestSummary_Id]"));
        Assert.That(clustered.Unique, Is.True);
        Assert.That(clustered.Clustered, Is.True);
        Assert.That(clustered.IndexColumns, Does.Contain("[Id]"));

        var nonclustered = view.Indexes[1];
        Assert.That(nonclustered.Name, Is.EqualTo("[IX_vTestSummary_Name]"));
        Assert.That(nonclustered.Unique, Is.False);
        Assert.That(nonclustered.Clustered, Is.False);
        Assert.That(nonclustered.IndexColumns, Does.Contain("[Name]"));

        conn.Close();
    }

    [Test]
    public void ShouldHandleIndexedViewWithNoNonclusteredIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'NoNC')
    EXEC('CREATE SCHEMA [NoNC]')
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TABLE NoNC.Products (Id INT NOT NULL, Price DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_Products ON NoNC.Products (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE VIEW NoNC.vProductSummary WITH SCHEMABINDING AS
SELECT Id, COUNT_BIG(*) AS Cnt, SUM(Price) AS TotalPrice
FROM NoNC.Products
GROUP BY Id;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE UNIQUE CLUSTERED INDEX [IX_vProductSummary_Id] ON NoNC.vProductSummary (Id);
";
        cmd.ExecuteNonQuery();

        // Install the extraction function (via KindleOneFile so the GO-batched idempotent create is split).
        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateIndexedViewJson.sql", Platform.SqlServer);

        cmd.CommandText = "SELECT [SchemaSmith].[GenerateIndexedViewJson]('NoNC', 'vProductSummary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<SqlServerIndexedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo("[NoNC]"));
        Assert.That(view.Name, Is.EqualTo("[vProductSummary]"));
        Assert.That(view.Definition, Does.Contain("Id"));
        Assert.That(view.Indexes, Has.Count.EqualTo(1));
        Assert.That(view.Indexes[0].Clustered, Is.True);
        Assert.That(view.Indexes[0].Unique, Is.True);

        conn.Close();
    }

    [Test]
    public void ShouldHandleMultipleIndexedViews()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'Multi')
    EXEC('CREATE SCHEMA [Multi]')
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TABLE Multi.Orders (Id INT NOT NULL, CustomerId INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_Orders ON Multi.Orders (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE VIEW Multi.vOrdersByCustomer WITH SCHEMABINDING AS
SELECT CustomerId, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount
FROM Multi.Orders
GROUP BY CustomerId;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE UNIQUE CLUSTERED INDEX [IX_vOrdersByCustomer_CustId] ON Multi.vOrdersByCustomer (CustomerId);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE VIEW Multi.vOrderTotals WITH SCHEMABINDING AS
SELECT Id, Amount
FROM Multi.Orders;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE UNIQUE CLUSTERED INDEX [IX_vOrderTotals_Id] ON Multi.vOrderTotals (Id);
";
        cmd.ExecuteNonQuery();

        // Install the extraction function (via KindleOneFile so the GO-batched idempotent create is split).
        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateIndexedViewJson.sql", Platform.SqlServer);

        // Extract first view
        cmd.CommandText = "SELECT [SchemaSmith].[GenerateIndexedViewJson]('Multi', 'vOrdersByCustomer')";
        var json1 = cmd.ExecuteScalar()?.ToString();
        Assert.That(json1, Is.Not.Null.And.Not.Empty);
        var view1 = JsonConvert.DeserializeObject<SqlServerIndexedView>(json1);
        Assert.That(view1, Is.Not.Null);
        Assert.That(view1.Name, Is.EqualTo("[vOrdersByCustomer]"));

        // Extract second view
        cmd.CommandText = "SELECT [SchemaSmith].[GenerateIndexedViewJson]('Multi', 'vOrderTotals')";
        var json2 = cmd.ExecuteScalar()?.ToString();
        Assert.That(json2, Is.Not.Null.And.Not.Empty);
        var view2 = JsonConvert.DeserializeObject<SqlServerIndexedView>(json2);
        Assert.That(view2, Is.Not.Null);
        Assert.That(view2.Name, Is.EqualTo("[vOrderTotals]"));

        conn.Close();
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE [{_integrationDb}];
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, dbName);
    }
}
