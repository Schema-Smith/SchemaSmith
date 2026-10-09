// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using log4net;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class DataTongsTests
{
    private string _integrationDb = "";
    private string _connectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test",null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("DataTongs");

        CreateTestDatabases();
    }

    [Test]
    [NonParallelizable]
    public void SchouldTongTableData()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP TABLE IF EXISTS public.""TestTable"";
CREATE TABLE public.""TestTable"" (
  ""Id"" INT NOT NULL PRIMARY KEY,
  ""Name"" VARCHAR(100) NOT NULL,
  ""Description"" VARCHAR(500) NULL,
  ""CreatedDate"" DATE NOT NULL DEFAULT CURRENT_DATE
);

INSERT INTO public.""TestTable"" (""Id"", ""Name"", ""Description"") 
  VALUES (1, 'Test Item 1', 'This is a test item.'),
         (2, 'Test Item 2', 'This is another test item.'),
         (3, 'Test Item 3', 'This is yet another test item.');
";
        cmd.ExecuteNonQuery();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);


            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["PostgreSQL:Port"];
            config["Source:User"] = config["PostgreSQL:User"];
            config["Source:Password"] = config["PostgreSQL:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputScripts"] = "true";

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            file.Received(2).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("public.TestTable.tabledata")), Arg.Is<string>(s => s.ContainsIgnoringCase("\"Description\":\"This is a test item.\",\"Id\":1,\"Name\":\"Test Item 1\"}")));
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("Populate public.TestTable.sql")), Arg.Is<string>(s => s.ContainsIgnoringCase("v_json JSON = '{{public.TestTable.tabledata}}';")));
            errorLog.DidNotReceive().Error(Arg.Any<string>());
            progressLog.DidNotReceive().Error(Arg.Is<string>(s => s.ContainsIgnoringCase("No match columns found")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void SchouldLogWhenUnableToDetectKeyColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP TABLE IF EXISTS public.""TestTable"";
CREATE TABLE public.""TestTable"" (
  ""Id"" INT NOT NULL, -- No primary key defined
  ""Name"" VARCHAR(100) NOT NULL,
  ""Description"" VARCHAR(500) NULL,
  ""CreatedDate"" DATE NOT NULL DEFAULT CURRENT_DATE
);

INSERT INTO public.""TestTable"" (""Id"", ""Name"", ""Description"") 
  VALUES (1, 'Test Item 1', 'This is a test item.'),
         (2, 'Test Item 2', 'This is another test item.'),
         (3, 'Test Item 3', 'This is yet another test item.');
";
        cmd.ExecuteNonQuery();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["PostgreSQL:Port"];
            config["Source:User"] = config["PostgreSQL:User"];
            config["Source:Password"] = config["PostgreSQL:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputScripts"] = "true";

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            // A skipped table is reported in the error log too, and the run reports itself incomplete.
            errorLog.Received(1).Error(Arg.Is<string>(s => s.ContainsIgnoringCase("No match columns found for public.TestTable. Skipping table.")));
            progressLog.Received(1).Error(Arg.Is<string>(s => s.ContainsIgnoringCase("  No match columns found for public.TestTable. Skipping table.")));
            Assert.That(tongs.ExitCode, Is.EqualTo(1));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void SchouldTongTableDataWithoutScript()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP TABLE IF EXISTS public.""TestTable"";
CREATE TABLE public.""TestTable"" (
  ""Id"" INT NOT NULL PRIMARY KEY,
  ""Name"" VARCHAR(100) NOT NULL,
  ""Description"" VARCHAR(500) NULL,
  ""CreatedDate"" DATE NOT NULL DEFAULT CURRENT_DATE
);

INSERT INTO public.""TestTable"" (""Id"", ""Name"", ""Description"") 
  VALUES (1, 'Test Item 1', 'This is a test item.'),
         (2, 'Test Item 2', 'This is another test item.'),
         (3, 'Test Item 3', 'This is yet another test item.');
";
        cmd.ExecuteNonQuery();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);


            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["PostgreSQL:Port"];
            config["Source:User"] = config["PostgreSQL:User"];
            config["Source:Password"] = config["PostgreSQL:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputScripts"] = "false";

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            file.Received(1).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("public.TestTable.tabledata")), Arg.Is<string>(s => s.ContainsIgnoringCase("\"Description\":\"This is a test item.\",\"Id\":1,\"Name\":\"Test Item 1\"}")));
            errorLog.DidNotReceive().Error(Arg.Any<string>());
            progressLog.DidNotReceive().Error(Arg.Is<string>(s => s.ContainsIgnoringCase("No match columns found")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }
    
    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"CREATE DATABASE ""{_integrationDb}"";";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{dbName}"";";
        cmd.ExecuteNonQuery();
    }
}
