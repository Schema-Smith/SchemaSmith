// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// An extracted trigger carries FOLLOWS rebuilt from ACTION_ORDER, and the files deploy in name order, so a follower
/// whose file sorts before its predecessor fails on the first pass and must succeed on the retry pass. On MariaDB the
/// failure is reported as error 4031, which the driver takes for a dropped session, so the runner also has to reopen
/// the connection before that retry.
/// </summary>
[NonParallelizable]
public abstract class TriggerOrderDeploySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string ConfigPrefix { get; }

    private const string TableName = "trigger_order_deploy";
    private IDbConnection _connection = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        Exec($"DROP TABLE IF EXISTS `{MainDb}`.`{TableName}`");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            Exec($"DROP TRIGGER IF EXISTS `{MainDb}`.`a_second`");
            Exec($"DROP TRIGGER IF EXISTS `{MainDb}`.`z_first`");
            Exec($"DROP TABLE IF EXISTS `{MainDb}`.`{TableName}`");
        }
        finally
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    [Test]
    public void AFollowerWhoseFileSortsFirst_DeploysOnTheRetryPass_AndKeepsItsOrder()
    {
        Assert.That(Deploy(), Is.True, "the deploy must succeed once the retry pass has created the follower");

        Assert.That(Scalar($"SELECT GROUP_CONCAT(CONCAT(TRIGGER_NAME, '=', ACTION_ORDER) ORDER BY ACTION_ORDER) FROM INFORMATION_SCHEMA.TRIGGERS " +
                           $"WHERE TRIGGER_SCHEMA = '{MainDb}' AND EVENT_OBJECT_TABLE = '{TableName}'"),
            Is.EqualTo("z_first=1,a_second=2"), "the follower must fire after its predecessor");
    }

    private bool Deploy()
    {
        var packageDir = Path.Join(Path.GetTempPath(), $"TriggerOrder_{Guid.NewGuid():N}");
        var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
        var templateDir = Path.Join(packageDir, "Templates", "Main");
        Directory.CreateDirectory(Path.Join(templateDir, "Tables"));
        Directory.CreateDirectory(Path.Join(templateDir, "Triggers"));
        File.WriteAllText(Path.Join(packageDir, "Product.json"), $$"""
            { "Name": "TriggerOrderProduct", "Platform": "{{Platform}}", "TemplateOrder": [ "Main" ] }
            """);
        File.WriteAllText(Path.Join(templateDir, "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT '{{MainDb}}'",
              "ScriptFolders": [ { "FolderPath": "Triggers", "QuenchSlot": "AfterTablesObjects" } ] }
            """);
        File.WriteAllText(Path.Join(templateDir, "Tables", $"{TableName}.json"), $$"""
            { "Name": "`{{TableName}}`", "Columns": [ { "Name": "`x`", "DataType": "INT", "Nullable": false }, { "Name": "`w`", "DataType": "INT", "Nullable": true } ],
              "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`x`" } ] }
            """);
        // The follower's file sorts first, so the first pass runs it before its predecessor exists.
        File.WriteAllText(Path.Join(templateDir, "Triggers", "a_second.sql"),
            "DROP TRIGGER IF EXISTS `a_second`;\nDELIMITER //\n" +
            $"CREATE TRIGGER `a_second` BEFORE INSERT ON `{TableName}` FOR EACH ROW FOLLOWS `z_first` SET NEW.w = 2 //\nDELIMITER ;\n");
        File.WriteAllText(Path.Join(templateDir, "Triggers", "z_first.sql"),
            "DROP TRIGGER IF EXISTS `z_first`;\nDELIMITER //\n" +
            $"CREATE TRIGGER `z_first` BEFORE INSERT ON `{TableName}` FOR EACH ROW SET NEW.w = 1 //\nDELIMITER ;\n");

        lock (FactoryContainer.SharedLockObject)
        {
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                var settings = new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = packageDir,
                    ["Target:User"] = config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"] ?? "3306",
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1"
                };
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    settings[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(settings!).Build());

                var product = Product.Load();
                var template = Template.Load("Main", product);
                var quench = new DatabaseQuench("127.0.0.1", product, template, MainDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: true,
                    deliverData: false, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();
                return quench.QuenchSuccessful;
            }
            finally
            {
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(packageDir)) Directory.Delete(packageDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
}
