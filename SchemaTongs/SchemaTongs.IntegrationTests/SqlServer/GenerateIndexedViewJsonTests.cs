// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data.Common;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Newtonsoft.Json;

namespace SchemaTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class GenerateIndexedViewJsonTests
{
    // Runs on the Schema suite's shared kindled database (already carrying GenerateIndexedViewJson), isolated
    // by fixture-owned schemas. Each test keeps a schema of its own, as it did before.
    private readonly string _p = "ivj_" + Guid.NewGuid().ToString("N")[..8];
    private string TestSchema => _p;
    private string NoNcSchema => _p + "_nonc";
    private string MultiSchema => _p + "_multi";
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _testConnectionString = Schema.IntegrationTests.SqlServer.FixtureSetup.GetMainDbConnectionString();
        DropFixtureSchemas();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForIndexedView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
EXEC('CREATE SCHEMA [{TestSchema}]')
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE TABLE [{TestSchema}].SourceTable (Id INT NOT NULL, Name VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_SourceTable ON [{TestSchema}].SourceTable (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE VIEW [{TestSchema}].vTestSummary WITH SCHEMABINDING AS
SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount
FROM [{TestSchema}].SourceTable
GROUP BY Id, Name;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE UNIQUE CLUSTERED INDEX [IX_vTestSummary_Id] ON [{TestSchema}].vTestSummary (Id);
CREATE NONCLUSTERED INDEX [IX_vTestSummary_Name] ON [{TestSchema}].vTestSummary (Name);
";
        cmd.ExecuteNonQuery();

        // Call it
        cmd.CommandText = $"SELECT [SchemaSmith].[GenerateIndexedViewJson]('{TestSchema}', 'vTestSummary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<SqlServerIndexedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo($"[{TestSchema}]"));
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

        cmd.CommandText = $@"
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{NoNcSchema}')
    EXEC('CREATE SCHEMA [{NoNcSchema}]')
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE TABLE [{NoNcSchema}].Products (Id INT NOT NULL, Price DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_Products ON [{NoNcSchema}].Products (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE VIEW [{NoNcSchema}].vProductSummary WITH SCHEMABINDING AS
SELECT Id, COUNT_BIG(*) AS Cnt, SUM(Price) AS TotalPrice
FROM [{NoNcSchema}].Products
GROUP BY Id;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE UNIQUE CLUSTERED INDEX [IX_vProductSummary_Id] ON [{NoNcSchema}].vProductSummary (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"SELECT [SchemaSmith].[GenerateIndexedViewJson]('{NoNcSchema}', 'vProductSummary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<SqlServerIndexedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo($"[{NoNcSchema}]"));
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

        cmd.CommandText = $@"
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{MultiSchema}')
    EXEC('CREATE SCHEMA [{MultiSchema}]')
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE TABLE [{MultiSchema}].Orders (Id INT NOT NULL, CustomerId INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_Orders ON [{MultiSchema}].Orders (Id);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE VIEW [{MultiSchema}].vOrdersByCustomer WITH SCHEMABINDING AS
SELECT CustomerId, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount
FROM [{MultiSchema}].Orders
GROUP BY CustomerId;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE UNIQUE CLUSTERED INDEX [IX_vOrdersByCustomer_CustId] ON [{MultiSchema}].vOrdersByCustomer (CustomerId);
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE VIEW [{MultiSchema}].vOrderTotals WITH SCHEMABINDING AS
SELECT Id, Amount
FROM [{MultiSchema}].Orders;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
CREATE UNIQUE CLUSTERED INDEX [IX_vOrderTotals_Id] ON [{MultiSchema}].vOrderTotals (Id);
";
        cmd.ExecuteNonQuery();

        // Extract first view
        cmd.CommandText = $"SELECT [SchemaSmith].[GenerateIndexedViewJson]('{MultiSchema}', 'vOrdersByCustomer')";
        var json1 = cmd.ExecuteScalar()?.ToString();
        Assert.That(json1, Is.Not.Null.And.Not.Empty);
        var view1 = JsonConvert.DeserializeObject<SqlServerIndexedView>(json1);
        Assert.That(view1, Is.Not.Null);
        Assert.That(view1.Name, Is.EqualTo("[vOrdersByCustomer]"));

        // Extract second view
        cmd.CommandText = $"SELECT [SchemaSmith].[GenerateIndexedViewJson]('{MultiSchema}', 'vOrderTotals')";
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
        try
        {
            DropFixtureSchemas();
        }
        catch (DbException) { /* best-effort cleanup must not mask a test result */ }
    }

    private void DropFixtureSchemas()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var schema in new[] { TestSchema, NoNcSchema, MultiSchema })
            Schema.IntegrationTests.SqlServer.TestSchemaDrop.Drop(cmd, schema);
        conn.Close();
    }
}
