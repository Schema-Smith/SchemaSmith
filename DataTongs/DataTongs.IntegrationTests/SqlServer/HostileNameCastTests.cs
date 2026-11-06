// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using DataTongs.IntegrationTests.Shared;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace DataTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class HostileNameCastTests : HostileNameCastSharedTests
{
    private string _database = "";
    private string _serverConnection = "";
    private string _databaseConnection = "";

    protected override Platform Platform => Platform.SqlServer;
    protected override string ConfigPrefix => "SqlServer";
    protected override string Database => _database;
    protected override string DatabaseConnectionString => _databaseConnection;
    protected override string Schema => "s" + H;
    protected override string H => "a'b%c[d]e.f\\g,h i éΩ";
    protected override string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    [OneTimeSetUp]
    public void CreateTheDatabase()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _database = $"DTHostile_{Guid.NewGuid():N}"[..30];
        var props = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _serverConnection = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"],
            config["SqlServer:Password"], config["SqlServer:Port"], props);
        _databaseConnection = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], _database, config["SqlServer:User"],
            config["SqlServer:Password"], config["SqlServer:Port"], props);
        using var connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_serverConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{_database}]";
        command.ExecuteNonQuery();
    }

    [OneTimeTearDown]
    public void DropTheDatabase()
    {
        using var connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_serverConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        global::Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(command, _database);
    }

    protected override void CreateSchema(IDbCommand command)
    {
        command.CommandText = $"EXEC(N'CREATE SCHEMA {Quote(Schema).Replace("'", "''")}')";
        command.ExecuteNonQuery();
    }

    protected override void DropSchema(IDbCommand command)
    {
        command.CommandText = $"EXEC(N'DROP SCHEMA {Quote(Schema).Replace("'", "''")}')";
        command.ExecuteNonQuery();
    }
}
