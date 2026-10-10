// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.IO;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using log4net;
using NSubstitute;

namespace SchemaTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Validation")]
public class ScriptValidationTests
{
    private string _integrationDb = "";
    private string _connectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("TongsValidation");

        CreateTestDatabase();
    }

    [Test]
    public void ValidScript_ProducesSqlFile()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        file.Exists(Arg.Any<string>()).Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:ValidateScripts"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // Valid view produces a .sql file
            file.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Views", "dbo.vw_ValidTest.sql"))),
                Arg.Any<string>());

            // No .sqlerror for the valid view
            file.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("vw_ValidTest.sqlerror")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void InvalidScript_ProducesSqulerrorFile()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        file.Exists(Arg.Any<string>()).Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:ValidateScripts"] = "true";
            config["ShouldCast:SaveInvalidScripts"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // Invalid view produces a .sqlerror file
            file.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.vw_InvalidTest.sqlerror")),
                Arg.Any<string>());

            // The .sql file is deleted after validation fails
            file.Received().Delete(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.vw_InvalidTest.sql")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void InvalidScript_GeneratesCleanupScript()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        file.Exists(Arg.Any<string>()).Returns(false);
        directory.Exists(Arg.Any<string>()).Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:ValidateScripts"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // Cleanup script is generated in Logs directory
            file.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Logs", "_InvalidObjectCleanup.sql"))),
                Arg.Is<string>(s => s.Contains("DROP VIEW") && s.Contains("[dbo].[vw_InvalidTest]")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SaveInvalidScripts_False_NoSqulerrorFile()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        file.Exists(Arg.Any<string>()).Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:ValidateScripts"] = "true";
            config["ShouldCast:SaveInvalidScripts"] = "false";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // No .sqlerror file when SaveInvalidScripts is false
            file.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase(".sqlerror")),
                Arg.Any<string>());

            // The .sql file for the invalid view is still deleted
            file.Received().Delete(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.vw_InvalidTest.sql")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void UnchangedKnownBad_SkipsValidation()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        // Simulate an existing .sqlerror file with the same content as the extracted script
        var invalidViewScript = "";
        file.Exists(Arg.Any<string>()).Returns(callInfo =>
        {
            var path = callInfo.ArgAt<string>(0);
            return path.EndsWithIgnoringCase("dbo.vw_InvalidTest.sqlerror");
        });
        file.ReadAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.vw_InvalidTest.sqlerror")))
            .Returns(callInfo => invalidViewScript);

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:ValidateScripts"] = "true";

            // First extraction: capture the invalid view script content
            file.When(f => f.WriteAllText(
                    Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.vw_InvalidTest.sql")),
                    Arg.Any<string>()))
                .Do(callInfo => invalidViewScript = callInfo.ArgAt<string>(1));

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // Verify the skip message was logged
            progressLog.Received().Info(
                Arg.Is<string>(s => s.Contains("Skipping validation") && s.Contains("vw_InvalidTest")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private IConfigurationRoot SetupConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);

        config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config["SqlServer:Port"];
        config["Source:User"] = config["SqlServer:User"];
        config["Source:Password"] = config["SqlServer:Password"];
        config["Source:database"] = _integrationDb;
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        foreach (var prop in connProps)
            config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        config["ShouldCast:Tables"] = "false";
        config["ShouldCast:Schemas"] = "false";
        config["ShouldCast:UserDefinedTypes"] = "false";
        config["ShouldCast:Functions"] = "false";
        config["ShouldCast:Views"] = "false";
        config["ShouldCast:Procedures"] = "false";
        config["ShouldCast:TableTriggers"] = "false";
        config["ShouldCast:Catalogs"] = "false";
        config["ShouldCast:StopLists"] = "false";
        config["ShouldCast:DDLTriggers"] = "false";
        config["ShouldCast:XMLSchemaCollections"] = "false";
        config["ShouldCast:IndexedViews"] = "false";
        FactoryContainer.Register<IConfigurationRoot>(config);
        return config;
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}];";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Create a table that a view will reference
        cmd.CommandText = @"
CREATE TABLE [dbo].[RefTable] ([Id] INT NOT NULL, [Name] VARCHAR(100) NULL);
";
        cmd.ExecuteNonQuery();

        // Create a valid view
        cmd.CommandText = @"
CREATE VIEW [dbo].[vw_ValidTest] AS SELECT 1 AS Val;
";
        cmd.ExecuteNonQuery();

        // Create an invalid view: references a table, then drop the table
        cmd.CommandText = @"
CREATE VIEW [dbo].[vw_InvalidTest] AS SELECT [Id], [Name] FROM [dbo].[RefTable];
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DROP TABLE [dbo].[RefTable];";
        cmd.ExecuteNonQuery();

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
        // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        cmd.CommandText = @$"
IF DB_ID('{dbName}') IS NOT NULL
  ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{dbName}];
";
        cmd.ExecuteNonQuery();
    }
}
