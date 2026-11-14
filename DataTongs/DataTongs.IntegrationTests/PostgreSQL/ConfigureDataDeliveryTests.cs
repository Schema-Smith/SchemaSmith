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

namespace DataTongs.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class ConfigureDataDeliveryTests
{
    private string _integrationDb = "";
    private string _connectionString = "";

    private static readonly string TemplateRoot = Path.Combine(Path.GetTempPath(), "schemasmith_test_template_pg");
    private static readonly string ContentDir = Path.Combine(TemplateRoot, "Content");
    private static readonly string TemplateJsonPath = Path.Combine(TemplateRoot, "Template.json");
    private static readonly string TableJsonPath = Path.Combine(TemplateRoot, "Tables", "public.TestTable.json");

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("datatongscdd");

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
              "Schema": "public",
              "Name": "TestTable",
              "Columns": [
                { "Name": "Id", "DataType": "INTEGER", "Nullable": false },
                { "Name": "Name", "DataType": "VARCHAR(100)", "Nullable": false }
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
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            file.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("public.TestTable.tabledata")),
                Arg.Any<string>());

            file.Received(1).WriteAllText(
                TableJsonPath,
                Arg.Is<string>(s => DataDeliveryHas(s, "Content/public.TestTable.tabledata", "Insert/Update")));

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

        var tableJson = """
            {
              "Schema": "public",
              "Name": "TestTable",
              "Columns": [
                { "Name": "Id", "DataType": "INTEGER", "Nullable": false },
                { "Name": "Name", "DataType": "VARCHAR(100)", "Nullable": false }
              ],
              "DataDelivery": {
                "ContentFile": "Content/public.TestTable.tabledata",
                "MergeType": "Insert/Update",
                "MergeUpdateDescendents": true
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
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            file.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("public.TestTable.tabledata")),
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
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.PostgreSQL);
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
            config["Tables:0:Name"] = "public.TestTable";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            progressLog.Received().Warn(Arg.Is<string>(s => s.ContainsIgnoringCase("not within a template")));

            file.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWithIgnoringCase("public.TestTable.tabledata")),
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
              "Schema": "public",
              "Name": "TestTable",
              "Columns": [
                { "Name": "Id", "DataType": "INTEGER", "Nullable": false }
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
            config["Tables:0:Name"] = "public.TestTable";
            config["Tables:0:MergeType"] = "Insert/Update/Delete";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.PostgreSQL);
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
              "Schema": "public",
              "Name": "TestTable",
              "Columns": [
                { "Name": "Id", "DataType": "INTEGER", "Nullable": false },
                { "Name": "Name", "DataType": "VARCHAR(100)", "Nullable": false }
              ],
              "DataDelivery": [
                {
                  "ContentFile": "Content/public.TestTable.eu.stale.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU'",
                  "VariantName": "EU"
                },
                {
                  "ContentFile": "Content/public.TestTable.us.tabledata",
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
            config["Tables:0:Name"] = "public.TestTable";
            config["Tables:0:VariantName"] = "EU";
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "false";
            config["ShouldCast:ConfigureDataDelivery"] = "true";
            config["ShouldCast:MergeDelete"] = "false";
            config["ContentPath"] = ContentDir;

            var tongs = new DataTongs(Platform.PostgreSQL);
            tongs.CastData();

            file.Received(1).WriteAllText(
                TableJsonPath,
                Arg.Is<string>(s =>
                    DataDeliveryHasVariant(s, "EU", "Content/public.TestTable.tabledata", "Insert/Update", "Target.Region == 'EU'") &&
                    DataDeliveryHasVariant(s, "US", "Content/public.TestTable.us.tabledata", "Insert", "Target.Region == 'US'")));

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
        config["Source:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config["PostgreSQL:Port"];
        config["Source:User"] = config["PostgreSQL:User"];
        config["Source:Password"] = config["PostgreSQL:Password"];
        config["Source:Database"] = _integrationDb;
        foreach (var prop in ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"))
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
        FactoryContainer.Unregister<IMergeScriptHelper>();
    }

    private void SetupTestTable()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        var dbConnectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], _integrationDb, config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(dbConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
DROP TABLE IF EXISTS public."TestTable";
CREATE TABLE public."TestTable" (
  "Id" INTEGER NOT NULL PRIMARY KEY,
  "Name" VARCHAR(100) NOT NULL
);
INSERT INTO public."TestTable" ("Id", "Name") VALUES (1, 'Test');
""";
        cmd.ExecuteNonQuery();
    }

    private static string GenerateUniqueDBName(string prefix)
    {
        var unique = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 8);
        return $"{prefix}_test_{DateTime.Now:yyyyMMdd_HHmmss}_{unique}".ToLowerInvariant();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();
    }

    // Two commands: PostgreSQL refuses DROP DATABASE inside a multi-statement batch.
    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid()";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $@"DROP DATABASE IF EXISTS ""{_integrationDb}""";
        cmd.ExecuteNonQuery();
    }
}
