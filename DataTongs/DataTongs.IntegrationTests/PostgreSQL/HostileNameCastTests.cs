// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using DataTongs.IntegrationTests.Shared;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace DataTongs.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class HostileNameCastTests : HostileNameCastSharedTests
{
    private string _database = "";
    private string _databaseConnection = "";
    private string _serverConnection = "";

    protected override Platform Platform => Platform.PostgreSQL;
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string Database => _database;
    protected override string DatabaseConnectionString => _databaseConnection;
    protected override string Schema => "s" + H;
    protected override string H => "a'b%c\"d.e\\f,g h éΩ";
    protected override string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    [OneTimeSetUp]
    public void CreateTheDatabase()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var props = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _database = $"dthostile_{Guid.NewGuid():N}"[..30];
        _serverConnection = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"],
            config["PostgreSQL:Password"], config["PostgreSQL:Port"], props);
        _databaseConnection = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], _database, config["PostgreSQL:User"],
            config["PostgreSQL:Password"], config["PostgreSQL:Port"], props);
        Server($"CREATE DATABASE \"{_database}\"");
    }

    [OneTimeTearDown]
    public void DropTheDatabase()
    {
        Server($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_database}' AND pid <> pg_backend_pid()");
        Server($"DROP DATABASE IF EXISTS \"{_database}\"");
    }

    private void Server(string sql)
    {
        using var connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_serverConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    protected override void CreateSchema(IDbCommand command)
    {
        command.CommandText = $"CREATE SCHEMA {Quote(Schema)}";
        command.ExecuteNonQuery();
    }

    protected override void DropSchema(IDbCommand command)
    {
        command.CommandText = $"DROP SCHEMA IF EXISTS {Quote(Schema)} CASCADE";
        command.ExecuteNonQuery();
    }
}
