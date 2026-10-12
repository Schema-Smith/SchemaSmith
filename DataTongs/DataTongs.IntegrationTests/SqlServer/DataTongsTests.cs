// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using log4net;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class DataTongsTests
{
    private string _integrationDb = "";
    private string _connectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test",null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("DataTongs");

        CreateTestDatabases();
    }

    // Graph node and edge tables carry internal columns (graph_id_..., $node_id_..., $from_id_... and more) that cannot be
    // selected or inserted, and every roster listed them, so neither kind could be extracted. A node table's own columns
    // now extract and deliver -- the engine assigns $node_id. An edge table's endpoints are another node's ids, which a
    // delivery cannot translate yet, so it is skipped with a warning and the run reports itself incomplete.
    [Test]
    [NonParallelizable]
    public void GraphTables_NodeExtractsAndDelivers_EdgeIsSkippedWithAWarning()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)), 4) AS INT)";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 14) Assert.Ignore("Graph tables need SQL Server 2017.");
        cmd.CommandText = @"
DROP TABLE IF EXISTS [dbo].[GEdge]; DROP TABLE IF EXISTS [dbo].[GNode]; DROP TABLE IF EXISTS [dbo].[GNodeCopy];
CREATE TABLE [dbo].[GNode] ([Id] INT NOT NULL PRIMARY KEY, [Name] NVARCHAR(20) NULL) AS NODE;
CREATE TABLE [dbo].[GNodeCopy] ([Id] INT NOT NULL PRIMARY KEY, [Name] NVARCHAR(20) NULL) AS NODE;
CREATE TABLE [dbo].[GEdge] ([Weight] INT NULL) AS EDGE;
INSERT [dbo].[GNode] ([Id], [Name]) VALUES (1, 'a'), (2, 'b');
INSERT [dbo].[GEdge] ($from_id, $to_id, [Weight]) SELECT f.$node_id, t.$node_id, 5 FROM [dbo].[GNode] f, [dbo].[GNode] t WHERE f.Id = 1 AND t.Id = 2;";
        cmd.ExecuteNonQuery();

        var tongs = new DataTongs(Platform.SqlServer);
        var columns = tongs.GetSelectColumns(cmd, "dbo", "GNode");
        Assert.That(columns, Does.Not.Contain("graph_id").And.Not.Contain("$node_id"));
        Assert.That(DataTongs.GetSqlServerColumnInfo(cmd, "dbo", "GNode").Select(c => c.Name), Is.EquivalentTo(new[] { "Id", "Name" }));

        var json = tongs.GetTableDataJson(cmd, columns, "dbo", "GNode", "[Id]", null);
        cmd.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", "GNodeCopy", json, "[Id]",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT COUNT(*) FROM [dbo].[GNodeCopy] WHERE $node_id IS NOT NULL";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "the node rows deliver, each with an engine-assigned $node_id");

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var file = Substitute.For<IFile>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());

            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "dbo.GEdge";
            config["Tables:1:Name"] = "dbo.GNode";
            config["ShouldCast:OutputScripts"] = "false";

            var run = new DataTongs(Platform.SqlServer);
            run.CastData();

            file.DidNotReceive().WriteAllText(Arg.Is<string>(s => s.ContainsIgnoringCase("GEdge")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.GNode.tabledata")), Arg.Any<string>());
            progressLog.Received(1).Warn(Arg.Is<string>(s => s.ContainsIgnoringCase("dbo.GEdge") && s.ContainsIgnoringCase("edge")));
            Assert.That(run.ExitCode, Is.EqualTo(1), "a skipped table leaves the run incomplete");

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void SchouldTongTableData()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP TABLE IF EXISTS [dbo].[TestTable];
CREATE TABLE [dbo].[TestTable] (
  [Id] INT NOT NULL PRIMARY KEY,
  [Name] NVARCHAR(100) NOT NULL,
  [Description] NVARCHAR(500) NULL,
  [CreatedDate] DATE NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[TestTable] ([Id], [Name], Description) 
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
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputScripts"] = "true";

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.Received(2).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.TestTable.tabledata")), Arg.Is<string>(s => s.ContainsIgnoringCase("\"Description\":\"This is a test item.\",\"Id\":1,\"Name\":\"Test Item 1\"}")));
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("Populate dbo.TestTable.sql")), Arg.Is<string>(s => s.ContainsIgnoringCase("DECLARE @v_json NVARCHAR(MAX) = '{{dbo.TestTable.tabledata}}';")));
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP TABLE IF EXISTS [dbo].[TestTable];
CREATE TABLE [dbo].[TestTable] (
  [Id] INT NOT NULL, -- No primary key defined
  [Name] NVARCHAR(100) NOT NULL,
  [Description] NVARCHAR(500) NULL,
  [CreatedDate] DATE NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[TestTable] ([Id], [Name], Description) 
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
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputScripts"] = "true";

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            // A skipped table is reported in the error log too, and the run reports itself incomplete.
            errorLog.Received(1).Error(Arg.Is<string>(s => s.ContainsIgnoringCase("No match columns found for dbo.TestTable. Skipping table.")));
            progressLog.Received(1).Error(Arg.Is<string>(s => s.ContainsIgnoringCase("  No match columns found for dbo.TestTable. Skipping table.")));
            Assert.That(tongs.ExitCode, Is.EqualTo(1));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void SchouldTongTableDataWithoutScript()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP TABLE IF EXISTS [dbo].[TestTable];
CREATE TABLE [dbo].[TestTable] (
  [Id] INT NOT NULL PRIMARY KEY,
  [Name] NVARCHAR(100) NOT NULL,
  [Description] NVARCHAR(500) NULL,
  [CreatedDate] DATE NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[TestTable] ([Id], [Name], Description) 
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
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _integrationDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputScripts"] = "false";

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.Received(1).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.TestTable.tabledata")), Arg.Is<string>(s => s.ContainsIgnoringCase("\"Description\":\"This is a test item.\",\"Id\":1,\"Name\":\"Test Item 1\"}")));
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE [{_integrationDb}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};
";
        cmd.ExecuteNonQuery();

        conn.Close();
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
