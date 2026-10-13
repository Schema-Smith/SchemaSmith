// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;

using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for SchemaQuench connection handling.
/// Uses dynamically created test databases via FixtureSetup.
/// </summary>
[Category("Integration")]
public abstract class SchemaQuenchConnectionTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string BaseConnectionString { get; }

    private IDbConnection _connection;

    [SetUp]
    public void SetUp()
    {
        // Connect without specifying a database initially
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(BaseConnectionString);
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void Connection_CanConnect()
    {
        Assert.That(_connection.State, Is.EqualTo(System.Data.ConnectionState.Open));
    }

    [Test]
    public void Connection_CanExecuteSimpleQuery()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(Convert.ToInt32(result), Is.EqualTo(1));
    }

    [Test]
    public void Connection_CanRetrieveHostname()
    {
        // SchemaQuench uses SELECT @@hostname to identify the server
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT @@hostname";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.Not.Empty);
    }

    [Test]
    public void Connection_CanRetrieveVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT @@version";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        // MySQL version should start with a number
        Assert.That(result.ToString(), Does.Match(@"^\d+\.\d+"));
    }

    [Test]
    public void Connection_CanQueryInformationSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{MainDb}'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(Convert.ToInt32(result), Is.EqualTo(1), "Test database should exist");
    }

    [Test]
    public void Connection_CanSwitchDatabase()
    {
        using var command = _connection.CreateCommand();

        // Switch to test database
        command.CommandText = $"USE `{MainDb}`";
        command.ExecuteNonQuery();

        // Verify we're in the test database
        command.CommandText = "SELECT DATABASE()";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), CatalogName.Matches(_connection, MainDb));
    }

    [Test]
    public void DbConnectionFactory_CanCreateConnection()
    {
        var factory = DbConnectionFactory.ForPlatform(Platform);

        using var connection = factory.GetDbConnection(MainConnectionString);
        connection.Open();

        Assert.That(connection.State, Is.EqualTo(System.Data.ConnectionState.Open));
    }

    [Test]
    public void ConnectionStringBuilder_BuildsCorrectFormat()
    {
        var connString = Schema.DataAccess.ConnectionString.Build(Platform, "localhost:3306", "testdb", "user", "pass");

        Assert.That(connString, Does.Contain("Server=localhost:3306"));
        Assert.That(connString, Does.Contain("Database=testdb"));
        Assert.That(connString, Does.Contain("Uid=user"));
        Assert.That(connString, Does.Contain("Pwd=pass"));
        Assert.That(connString, Does.Contain("AllowUserVariables=true"));
    }
}
