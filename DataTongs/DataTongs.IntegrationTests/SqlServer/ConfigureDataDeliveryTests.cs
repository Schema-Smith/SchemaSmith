// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using DataTongs.IntegrationTests.Support;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.SqlServer;

/// <summary>
/// Integration tests for DataTongs' --ConfigureDataDelivery path. Uses a real SQL Server
/// source database plus mocked IFile/IDirectory abstractions so the configurator can be
/// observed without touching a real filesystem.
/// </summary>
[Category("SqlServer")]
public class ConfigureDataDeliveryTests
{
    private string _integrationDb = "";
    private string _connectionString = "";

    private static readonly string TemplateRoot = Path.Combine(Path.GetTempPath(), "schemasmith_test_template");
    private static readonly string ContentDir = Path.Combine(TemplateRoot, "Content");
    private static readonly string TemplateJsonPath = Path.Combine(TemplateRoot, "Template.json");
    private static readonly string TableJsonPath = Path.Combine(TemplateRoot, "Tables", "dbo.TestTable.json");

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("DataTongsCDD");

        CreateTestDatabases();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabases();
    }

    [Test]
    [NonParallelizable]
    public void ShouldUpdateTableJsonWithDataDeliverySettings()
    {
        SetupTestTable();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": false }
              ]
            }
            """;

        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.GetFiles(Arg.Any<string>(), "*.json", SearchOption.TopDirectoryOnly)
            .Returns(new[] { TableJsonPath });
        file.Exists(TemplateJsonPath).Returns(true);
        file.Exists(TableJsonPath).Returns(true);
        file.ReadAllText(TableJsonPath).Returns(tableJson);

        lock (FactoryContainer.SharedLockObject)
        {
            ResetStaticState();
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = SetupSourceConfig();
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.TestTable.tabledata")),
                Arg.Any<string>());

            file.Received(1).WriteAllText(
                TableJsonPath,
                Arg.Is<string>(s => DataDeliveryHas(s, "Content/dbo.TestTable.tabledata", "Insert/Update")));

            errorLog.DidNotReceive().Error(Arg.Any<string>());

            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<IFile>();
            FactoryContainer.Unregister<IDirectory>();
            RestoreConfig();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void ShouldSkipWriteWhenSettingsUnchanged()
    {
        SetupTestTable();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        var tableJson = $$"""
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": false }
              ],
              "DataDelivery": {
                "ContentFile": "Content/dbo.TestTable.tabledata",
                "MergeType": "Insert/Update"
              }
            }
            """;

        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.GetFiles(Arg.Any<string>(), "*.json", SearchOption.TopDirectoryOnly)
            .Returns(new[] { TableJsonPath });
        file.Exists(TemplateJsonPath).Returns(true);
        file.Exists(TableJsonPath).Returns(true);
        file.ReadAllText(TableJsonPath).Returns(tableJson);

        lock (FactoryContainer.SharedLockObject)
        {
            ResetStaticState();
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = SetupSourceConfig();
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.TestTable.tabledata")),
                Arg.Any<string>());

            file.DidNotReceive().WriteAllText(
                TableJsonPath,
                Arg.Any<string>());

            progressLog.Received().Info(Arg.Is<string>(s => s.ContainsIgnoringCase("already up to date")));

            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<IFile>();
            FactoryContainer.Unregister<IDirectory>();
            RestoreConfig();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void ShouldWarnWhenTableJsonNotFound()
    {
        SetupTestTable();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.GetFiles(Arg.Any<string>(), "*.json", SearchOption.TopDirectoryOnly)
            .Returns(Array.Empty<string>());
        file.Exists(TemplateJsonPath).Returns(true);

        lock (FactoryContainer.SharedLockObject)
        {
            ResetStaticState();
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = SetupSourceConfig();
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            progressLog.Received().Warn(Arg.Is<string>(s => s.ContainsIgnoringCase("Table.json not found")));

            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<IFile>();
            FactoryContainer.Unregister<IDirectory>();
            RestoreConfig();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void ShouldWarnAndDisableWhenTemplateJsonNotFound()
    {
        SetupTestTable();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        file.Exists(Arg.Any<string>()).Returns(false);
        directory.Exists(Arg.Any<string>()).Returns(true);

        lock (FactoryContainer.SharedLockObject)
        {
            ResetStaticState();
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = SetupSourceConfig();
            config["Tables:0:Name"] = "dbo.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            progressLog.Received().Warn(Arg.Is<string>(s => s.ContainsIgnoringCase("not within a template")));

            file.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("dbo.TestTable.tabledata")),
                Arg.Any<string>());

            file.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase(".json")),
                Arg.Any<string>());

            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<IFile>();
            FactoryContainer.Unregister<IDirectory>();
            RestoreConfig();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void ShouldUsePerTableMergeTypeOverride()
    {
        SetupTestTable();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false }
              ]
            }
            """;

        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.GetFiles(Arg.Any<string>(), "*.json", SearchOption.TopDirectoryOnly)
            .Returns(new[] { TableJsonPath });
        file.Exists(TemplateJsonPath).Returns(true);
        file.Exists(TableJsonPath).Returns(true);
        file.ReadAllText(TableJsonPath).Returns(tableJson);

        lock (FactoryContainer.SharedLockObject)
        {
            ResetStaticState();
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = SetupSourceConfig();
            config["Tables:0:Name"] = "dbo.TestTable";
            config["Tables:0:MergeType"] = "Insert/Update/Delete";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.Received(1).WriteAllText(
                TableJsonPath,
                Arg.Is<string>(s => s.ContainsIgnoringCase("\"MergeType\": \"Insert/Update/Delete\"")));

            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<IFile>();
            FactoryContainer.Unregister<IDirectory>();
            RestoreConfig();
            LogFactory.Clear();
        }
    }

    [Test]
    [NonParallelizable]
    public void ShouldReconcileMatchingVariantNameAndPreserveSiblingAndGates()
    {
        SetupTestTable();

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": false }
              ],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.eu.stale.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU'",
                  "VariantName": "EU"
                },
                {
                  "ContentFile": "Content/dbo.TestTable.us.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'US'",
                  "VariantName": "US"
                }
              ]
            }
            """;

        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.GetFiles(Arg.Any<string>(), "*.json", SearchOption.TopDirectoryOnly)
            .Returns(new[] { TableJsonPath });
        file.Exists(TemplateJsonPath).Returns(true);
        file.Exists(TableJsonPath).Returns(true);
        file.ReadAllText(TableJsonPath).Returns(tableJson);

        lock (FactoryContainer.SharedLockObject)
        {
            ResetStaticState();
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);

            var config = SetupSourceConfig();
            config["Tables:0:Name"] = "dbo.TestTable";
            config["Tables:0:VariantName"] = "EU";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.SqlServer);
            tongs.CastData();

            file.Received(1).WriteAllText(
                TableJsonPath,
                Arg.Is<string>(s =>
                    DataDeliveryHasVariant(s, "EU", "Content/dbo.TestTable.tabledata", "Insert/Update", "Target.Region == 'EU'") &&
                    DataDeliveryHasVariant(s, "US", "Content/dbo.TestTable.us.tabledata", "Insert", "Target.Region == 'US'")));

            errorLog.DidNotReceive().Error(Arg.Any<string>());

            FactoryContainer.Unregister<IEnvironment>();
            FactoryContainer.Unregister<IFile>();
            FactoryContainer.Unregister<IDirectory>();
            RestoreConfig();
            LogFactory.Clear();
        }
    }

    private static bool DataDeliveryHas(string tableJson, string contentFile, string mergeType)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(tableJson);
        if (!doc.RootElement.TryGetProperty("DataDelivery", out var dd)) return false;
        var variants = dd.ValueKind == System.Text.Json.JsonValueKind.Array
            ? dd.EnumerateArray()
            : new[] { dd }.AsEnumerable();
        return variants.Any(v =>
            v.TryGetProperty("ContentFile", out var cf) && cf.GetString() == contentFile &&
            v.TryGetProperty("MergeType", out var mt) && mt.GetString() == mergeType);
    }

    private static bool DataDeliveryHasVariant(string tableJson, string variantName, string contentFile, string mergeType, string shouldApply)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(tableJson);
        if (!doc.RootElement.TryGetProperty("DataDelivery", out var dd)) return false;
        var variants = dd.ValueKind == System.Text.Json.JsonValueKind.Array ? dd.EnumerateArray() : new[] { dd }.AsEnumerable();
        return variants.Any(v =>
            v.TryGetProperty("VariantName", out var vn) && vn.GetString() == variantName &&
            v.TryGetProperty("ContentFile", out var cf) && cf.GetString() == contentFile &&
            v.TryGetProperty("MergeType", out var mt) && mt.GetString() == mergeType &&
            v.TryGetProperty("ShouldApplyExpression", out var sa) && sa.GetString() == shouldApply);
    }

    private IsolatedConfigScope _configScope;

    private IConfigurationRoot SetupSourceConfig()
    {
        ConfigHelper.GetAppSettingsAndUserSecrets("test", null); // ensure the base config is registered before snapshot
        _configScope = IsolatedConfigScope.Create();
        var config = _configScope.Config;
        config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config["SqlServer:Port"];
        config["Source:User"] = config["SqlServer:User"];
        config["Source:Password"] = config["SqlServer:Password"];
        config["Source:Database"] = _integrationDb;
        foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
            config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
        return config;
    }

    private void RestoreConfig()
    {
        _configScope?.Dispose();
        _configScope = null;
    }

    private static void ResetStaticState()
    {
        // Clear any state lingering from previous tests (platform-specific script helper caches, etc.).
        FactoryContainer.Unregister<IMergeScriptHelper>();
    }

    private void SetupTestTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
DROP TABLE IF EXISTS [dbo].[TestTable];
CREATE TABLE [dbo].[TestTable] (
  [Id] INT NOT NULL PRIMARY KEY,
  [Name] NVARCHAR(100) NOT NULL
);
INSERT INTO [dbo].[TestTable] ([Id], [Name]) VALUES (1, 'Test');
""";
        cmd.ExecuteNonQuery();
    }

    private static string GenerateUniqueDBName(string prefix)
    {
        var unique = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 8);
        return $"{prefix}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{unique}";
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};";
        cmd.ExecuteNonQuery();
    }

    private void DropTestDatabases()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _integrationDb);
        }
        catch { }
    }
}
