// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using DataTongs.IntegrationTests.Support;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.Shared;

/// <summary>
/// Real-filesystem integration tests for DataTongs' --ConfigureDataDelivery flow.
/// Exercises the JSON round-trip, template-root auto-discovery, and nested
/// DataDelivery property persistence end-to-end.
/// </summary>
public abstract class ConfigureDataDeliverySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string ConfigPrefix { get; }

    private IDbConnection _connection;
    private global::DataTongs.DataTongs _dataTongs = null!;
    private string _testOutputDir = null!;
    private string _testDb;

    [SetUp]
    public void SetUp()
    {
        _dataTongs = new global::DataTongs.DataTongs(Platform);
        _testOutputDir = Path.Combine(Path.GetTempPath(), $"DataTongsDelivery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testOutputDir);
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;

        if (Directory.Exists(_testOutputDir))
        {
            try { Directory.Delete(_testOutputDir, true); } catch { /* ignore */ }
        }
    }

    private void EnsureDbConnection()
    {
        if (_connection == null)
        {
            _testDb = MainDb;
            _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
            _connection.Open();
        }
    }

    #region Template Structure Helpers

    private string CreateTemplateStructure()
    {
        var templateRoot = _testOutputDir;
        File.WriteAllText(Path.Combine(templateRoot, "Template.json"), "{ \"Name\": \"TestTemplate\" }");

        var tablesDir = Path.Combine(templateRoot, "Tables");
        Directory.CreateDirectory(tablesDir);

        var tableDataDir = Path.Combine(templateRoot, "TableData");
        Directory.CreateDirectory(tableDataDir);

        return templateRoot;
    }

    private static void CreateTableJson(string tablesDir, string tableName)
    {
        var table = new MySqlTable { Name = tableName };
        JsonHelper.Write(Path.Combine(tablesDir, $"{tableName}.json"), table);
    }

    private Table LoadTableJson(string tablesDir, string tableName)
    {
        var path = Path.Combine(tablesDir, $"{tableName}.json");
        var json = File.ReadAllText(path);
        return PlatformDeserializer.DeserializeTable(json, Platform);
    }

    #endregion

    #region FindTemplateRoot Integration Tests

    [Test]
    public void FindTemplateRoot_FindsTemplateInRealFileSystem()
    {
        CreateTemplateStructure();
        var tableDataDir = Path.Combine(_testOutputDir, "TableData");

        var result = global::DataTongs.DataTongs.FindTemplateRootPath(tableDataDir);

        Assert.That(result, Is.EqualTo(_testOutputDir));
    }

    [Test]
    public void FindTemplateRoot_ReturnsNull_WhenNoTemplateExists()
    {
        var randomDir = Path.Combine(Path.GetTempPath(), $"NoTemplate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(randomDir);

        try
        {
            var result = global::DataTongs.DataTongs.FindTemplateRootPath(randomDir);
            Assert.That(result, Is.Null);
        }
        finally
        {
            Directory.Delete(randomDir, true);
        }
    }

    #endregion

    #region ConfigureDataDelivery End-to-End Tests

    [Test]
    public void ConfigureDataDelivery_EndToEnd_UpdatesTableJson()
    {
        EnsureDbConnection();
        using var command = _connection.CreateCommand();

        var templateRoot = CreateTemplateStructure();
        var tablesDir = Path.Combine(templateRoot, "Tables");
        var tableDataDir = Path.Combine(templateRoot, "TableData");

        CreateTableJson(tablesDir, "country");

        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "country");
        var json = _dataTongs.GetTableDataJson(command, "", _testDb, "country", keyColumns, "country_id <= 5");

        var contentFilePath = Path.Combine(tableDataDir, "country.tabledata");
        File.WriteAllText(contentFilePath, FormatJson(json));

        var table = LoadTableJson(tablesDir, "country");

        var relativePath = Path.GetRelativePath(templateRoot, contentFilePath).Replace('\\', '/');

        table.DataDelivery =
                [
                    new DataDelivery
                    {
            ContentFile = relativePath,
            MergeType = "Insert/Update",
            MergeDisableTriggers = false
        }
                ];

        JsonHelper.Write(Path.Combine(tablesDir, "country.json"), table);

        var updatedTable = LoadTableJson(tablesDir, "country");
        Assert.Multiple(() =>
        {
            Assert.That(updatedTable.DataDelivery, Is.Not.Null);
            Assert.That(updatedTable.DataDelivery[0].ContentFile, Is.EqualTo("TableData/country.tabledata"));
            Assert.That(updatedTable.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(updatedTable.DataDelivery[0].MergeDisableTriggers, Is.False);
        });
    }

    [Test]
    public void ConfigureDataDelivery_ContentFileRelativePath_UsesForwardSlashes()
    {
        var templateRoot = CreateTemplateStructure();
        var tableDataDir = Path.Combine(templateRoot, "TableData");

        var contentFilePath = Path.Combine(tableDataDir, "actor.tabledata");
        File.WriteAllText(contentFilePath, "[]");

        var relativePath = Path.GetRelativePath(templateRoot, contentFilePath).Replace('\\', '/');

        Assert.That(relativePath, Is.EqualTo("TableData/actor.tabledata"));
        Assert.That(relativePath, Does.Not.Contain("\\"));
    }

    [Test]
    public void ConfigureDataDelivery_WithMatchColumnsAndFilter_SetsProperties()
    {
        var templateRoot = CreateTemplateStructure();
        var tablesDir = Path.Combine(templateRoot, "Tables");

        CreateTableJson(tablesDir, "actor");

        var table = LoadTableJson(tablesDir, "actor");
        table.DataDelivery =
                [
                    new DataDelivery
                    {
            ContentFile = "TableData/actor.tabledata",
            MergeType = "Insert/Update/Delete",
            MatchColumns = "`code`",
            MergeFilter = "active = 1",
            MergeDisableTriggers = true
        }
                ];

        JsonHelper.Write(Path.Combine(tablesDir, "actor.json"), table);

        var updatedTable = LoadTableJson(tablesDir, "actor");
        Assert.Multiple(() =>
        {
            Assert.That(updatedTable.DataDelivery, Is.Not.Null);
            Assert.That(updatedTable.DataDelivery[0].MatchColumns, Is.EqualTo("`code`"));
            Assert.That(updatedTable.DataDelivery[0].MergeFilter, Is.EqualTo("active = 1"));
            Assert.That(updatedTable.DataDelivery[0].MergeDisableTriggers, Is.True);
        });
    }

    [Test]
    public void ConfigureDataDelivery_ClearsMatchColumnsAndFilter_WhenBlank()
    {
        var templateRoot = CreateTemplateStructure();
        var tablesDir = Path.Combine(templateRoot, "Tables");

        var tablePath = Path.Combine(tablesDir, "actor.json");
        var table = new MySqlTable
        {
            Name = "actor",
            DataDelivery =
                [
                    new DataDelivery
                    {
                MatchColumns = "`old_key`",
                MergeFilter = "old_filter = 1"
            }
                ]
        };
        JsonHelper.Write(tablePath, table);

        var loadedTable = LoadTableJson(tablesDir, "actor");
        loadedTable.DataDelivery[0].MatchColumns = null;
        loadedTable.DataDelivery[0].MergeFilter = null;
        JsonHelper.Write(tablePath, loadedTable);

        var updatedTable = LoadTableJson(tablesDir, "actor");
        Assert.Multiple(() =>
        {
            Assert.That(updatedTable.DataDelivery, Is.Not.Null);
            Assert.That(updatedTable.DataDelivery[0].MatchColumns, Is.Null);
            Assert.That(updatedTable.DataDelivery[0].MergeFilter, Is.Null);
        });

        var json = File.ReadAllText(tablePath);
        Assert.That(json, Does.Not.Contain("\"MatchColumns\""));
        Assert.That(json, Does.Not.Contain("\"MergeFilter\""));
    }

    [Test]
    public void ConfigureDataDelivery_TableJsonNotFound_ExtractionStillSucceeds()
    {
        EnsureDbConnection();
        using var command = _connection.CreateCommand();

        var templateRoot = CreateTemplateStructure();
        var tablesDir = Path.Combine(templateRoot, "Tables");

        Assert.That(File.Exists(Path.Combine(tablesDir, "country.json")), Is.False);

        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "country");
        var json = _dataTongs.GetTableDataJson(command, "", _testDb, "country", keyColumns, "country_id <= 5");

        Assert.That(json, Is.Not.Empty);
        Assert.That(json, Does.Contain("country_id"));
    }

    [Test]
    public void ConfigureDataDelivery_OverwriteExistingValues_UpdatesCorrectly()
    {
        var templateRoot = CreateTemplateStructure();
        var tablesDir = Path.Combine(templateRoot, "Tables");

        var tablePath = Path.Combine(tablesDir, "actor.json");
        var table = new MySqlTable
        {
            Name = "actor",
            DataDelivery =
                [
                    new DataDelivery
                    {
                ContentFile = "OldPath/actor.tabledata",
                MergeType = "Insert/Update/Delete",
                MergeDisableTriggers = true,
                MatchColumns = "`old_key`",
                MergeFilter = "old_filter = 1"
            }
                ]
        };
        JsonHelper.Write(tablePath, table);

        var loadedTable = LoadTableJson(tablesDir, "actor");
        loadedTable.DataDelivery[0].ContentFile = "TableData/actor.tabledata";
        loadedTable.DataDelivery[0].MergeType = "Insert/Update";
        loadedTable.DataDelivery[0].MergeDisableTriggers = false;
        loadedTable.DataDelivery[0].MatchColumns = "`new_key`";
        loadedTable.DataDelivery[0].MergeFilter = "new_filter = 1";
        JsonHelper.Write(tablePath, loadedTable);

        var updatedTable = LoadTableJson(tablesDir, "actor");
        Assert.Multiple(() =>
        {
            Assert.That(updatedTable.DataDelivery, Is.Not.Null);
            Assert.That(updatedTable.DataDelivery[0].ContentFile, Is.EqualTo("TableData/actor.tabledata"));
            Assert.That(updatedTable.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(updatedTable.DataDelivery[0].MergeDisableTriggers, Is.False);
            Assert.That(updatedTable.DataDelivery[0].MatchColumns, Is.EqualTo("`new_key`"));
            Assert.That(updatedTable.DataDelivery[0].MergeFilter, Is.EqualTo("new_filter = 1"));
        });
    }

    [Test]
    public void ConfigureDataDelivery_TableJsonPreservesOtherProperties()
    {
        var templateRoot = CreateTemplateStructure();
        var tablesDir = Path.Combine(templateRoot, "Tables");

        var tablePath = Path.Combine(tablesDir, "actor.json");
        var table = new MySqlTable
        {
            Name = "actor",
            Engine = "InnoDB",
            CharacterSet = "utf8mb4",
            Columns = new List<Column>
            {
                new Column { Name = "actor_id", DataType = "int" },
                new Column { Name = "first_name", DataType = "varchar" }
            }
        };
        JsonHelper.Write(tablePath, table);

        var loadedTable = LoadTableJson(tablesDir, "actor");
        loadedTable.DataDelivery =
                [
                    new DataDelivery
                    {
            ContentFile = "TableData/actor.tabledata",
            MergeType = "Insert/Update"
        }
                ];
        JsonHelper.Write(tablePath, loadedTable);

        var updatedTable = LoadTableJson(tablesDir, "actor");
        var updatedMySqlTable = updatedTable as MySqlTable;
        Assert.Multiple(() =>
        {
            Assert.That(updatedTable.Name, Is.EqualTo("actor"));
            Assert.That(updatedMySqlTable, Is.Not.Null);
            Assert.That(updatedMySqlTable!.Engine, Is.EqualTo("InnoDB"));
            Assert.That(updatedMySqlTable.CharacterSet, Is.EqualTo("utf8mb4"));
            Assert.That(updatedTable.Columns, Has.Count.EqualTo(2));
            Assert.That(updatedTable.DataDelivery, Is.Not.Null);
            Assert.That(updatedTable.DataDelivery[0].ContentFile, Is.EqualTo("TableData/actor.tabledata"));
            Assert.That(updatedTable.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
        });
    }

    [Test]
    public void ConfigureDataDelivery_AbsentMergeDelete_DoesNotDeriveTheDeleteBranch()
    {
        // The delete branch removes target rows the extracted source does not contain, so an ABSENT
        // MergeDelete must not switch it on. It used to: every ShouldCast flag was read with
        // `!= "false"`, which makes a missing key true, and MergeDelete is the one member of that
        // family where defaulting on destroys data. Nothing caught it because the shipped settings file
        // and every test set the key explicitly -- so the real default was never exercised.
        EnsureDbConnection();
        using var command = _connection.CreateCommand();

        var tableName = $"nodel_{Guid.NewGuid():N}".Substring(0, 20);
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                id INT PRIMARY KEY,
                name VARCHAR(50) NOT NULL
            )";
        command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO `{_testDb}`.`{tableName}` VALUES (1, 'Test')";
        command.ExecuteNonQuery();

        try
        {
            var templateRoot = CreateTemplateStructure();
            var tablesDir = Path.Join(templateRoot, "Tables");
            var tableDataDir = Path.Join(templateRoot, "TableData");
            var tablePath = Path.Join(tablesDir, $"{tableName}.json");

            File.WriteAllText(tablePath, $$"""
                {
                  "Name": "{{tableName}}",
                  "Columns": [
                    { "Name": "id", "DataType": "int" },
                    { "Name": "name", "DataType": "varchar" }
                  ]
                }
                """);

            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Unregister<IMergeScriptHelper>();
                ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                using (var configScope = IsolatedConfigScope.Create())
                {
                    var config = configScope.Config;
                    config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
                    config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
                    config["Source:User"] = config[$"{ConfigPrefix}:User"];
                    config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
                    config["Source:Database"] = _testDb;
                    config["Tables:0:Name"] = tableName;
                    config["ShouldCast:OutputContentFiles"] = "true";
                    config["ShouldCast:OutputScripts"] = "false";
                    config["ShouldCast:ConfigureDataDelivery"] = "true";
                    // ShouldCast:MergeDelete is DELIBERATELY not set -- that is the whole point.
                    config["ContentPath"] = tableDataDir;

                    _dataTongs.CastData();
                }
            }

            var updatedJson = File.ReadAllText(tablePath);
            Assert.That(updatedJson, Does.Not.Contain("Insert/Update/Delete"),
                "an absent MergeDelete must not derive a delete branch -- deleting the key is not a "
                + "request to delete target rows");
            Assert.That(updatedJson, Does.Contain("\"MergeType\": \"Insert/Update\""),
                "MergeUpdate still defaults on, so the derived type is Insert/Update");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    [NonParallelizable]
    public void ConfigureDataDelivery_ReconcilesMatchingVariantName_PreservesSiblingAndGates()
    {
        // Full DataTongs.CastData() -> DataDeliveryConfiguratorContext -> reconcile threading,
        // proven through the real CastData() entry point (matching this file's real-filesystem
        // convention, rather than the SqlServer/PostgreSQL suites' mocked-IFile convention).
        EnsureDbConnection();
        using var command = _connection.CreateCommand();

        var tableName = $"variant_{Guid.NewGuid():N}".Substring(0, 20);
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{tableName}` (
                id INT PRIMARY KEY,
                name VARCHAR(50) NOT NULL
            )";
        command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO `{_testDb}`.`{tableName}` VALUES (1, 'Test')";
        command.ExecuteNonQuery();

        try
        {
            var templateRoot = CreateTemplateStructure();
            var tablesDir = Path.Join(templateRoot, "Tables");
            var tableDataDir = Path.Join(templateRoot, "TableData");
            var tablePath = Path.Join(tablesDir, $"{tableName}.json");

            var tableJson = $$"""
                {
                  "Name": "{{tableName}}",
                  "Columns": [
                    { "Name": "id", "DataType": "int" },
                    { "Name": "name", "DataType": "varchar" }
                  ],
                  "DataDelivery": [
                    {
                      "ContentFile": "TableData/{{tableName}}.eu.stale.tabledata",
                      "MergeType": "Insert",
                      "ShouldApplyExpression": "Target.Region == 'EU'",
                      "VariantName": "EU"
                    },
                    {
                      "ContentFile": "TableData/{{tableName}}.us.tabledata",
                      "MergeType": "Insert",
                      "ShouldApplyExpression": "Target.Region == 'US'",
                      "VariantName": "US"
                    }
                  ]
                }
                """;
            File.WriteAllText(tablePath, tableJson);

            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Unregister<IMergeScriptHelper>();
                ConfigHelper.GetAppSettingsAndUserSecrets("test", null); // ensure the base config is registered before snapshot
                using (var configScope = IsolatedConfigScope.Create())
                {
                    var config = configScope.Config;
                    config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
                    config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
                    config["Source:User"] = config[$"{ConfigPrefix}:User"];
                    config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
                    config["Source:Database"] = _testDb;
                    config["Tables:0:Name"] = tableName;
                    config["Tables:0:VariantName"] = "EU";
                    config["ShouldCast:OutputContentFiles"] = "true";
                    config["ShouldCast:OutputScripts"] = "false";
                    config["ShouldCast:ConfigureDataDelivery"] = "true";
                    config["ShouldCast:MergeDelete"] = "false";
                    config["ContentPath"] = tableDataDir;

                    _dataTongs.CastData();
                }
            }

            var updatedJson = File.ReadAllText(tablePath);
            Assert.Multiple(() =>
            {
                Assert.That(updatedJson, Does.Contain($"\"ContentFile\": \"TableData/{tableName}.tabledata\""));
                Assert.That(updatedJson, Does.Contain("\"ShouldApplyExpression\": \"Target.Region == 'EU'\""));
                Assert.That(updatedJson, Does.Contain("\"VariantName\": \"EU\""));
                Assert.That(updatedJson, Does.Contain($"\"ContentFile\": \"TableData/{tableName}.us.tabledata\""));
                Assert.That(updatedJson, Does.Contain("\"MergeType\": \"Insert\""));
                Assert.That(updatedJson, Does.Contain("\"ShouldApplyExpression\": \"Target.Region == 'US'\""));
                Assert.That(updatedJson, Does.Contain("\"VariantName\": \"US\""));
            });
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Helper Methods

    private static string FormatJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]" || json == "null")
            return "[]";

        return json
            .Replace("},{", "},\n{")
            .Replace("[{", "[\n{")
            .Replace("}]", "}\n]");
    }

    #endregion
}
