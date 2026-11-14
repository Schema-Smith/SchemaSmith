// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Extensions.Configuration;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Utility;
using Index = Schema.Domain.Index;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A column with fractional seconds -- <c>DATETIME(6)</c>, <c>TIMESTAMP(6)</c> defaulting and refreshing to
/// <c>CURRENT_TIMESTAMP(6)</c>, <c>TIME(3)</c> -- is read back from the catalog in a form that differs from the declaration
/// (the precision moves between the type and the default). An unchanged redeploy must change nothing.
/// </summary>
[NonParallelizable]
public abstract class FractionalTemporalRedeploySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string ConfigPrefix { get; }

    private const string TableName = "frac_temporal_redeploy";

    [OneTimeTearDown]
    public void OneTimeTearDown() => Exec($"DROP TABLE IF EXISTS `{MainDb}`.`{TableName}`");

    [Test]
    public void FractionalSecondColumns_RedeployWithoutChange()
    {
        Exec($"DROP TABLE IF EXISTS `{MainDb}`.`{TableName}`");
        Assert.That(Deploy(), Is.True, "the first deploy");
        Exec($"DELETE FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");

        Assert.That(Deploy(), Is.True, "an unchanged redeploy");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar($"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'happened_at'"),
                Is.EqualTo("datetime(6)"), "premise: the column was created with its precision");
            Assert.That(Scalar($"SELECT GROUP_CONCAT(CONCAT(ObjectType, ' ', ObjectName, ' ', ActionType) SEPARATOR '; ') FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'"),
                Is.Null, "an unchanged redeploy changes nothing");
        });
    }

    private bool Deploy()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Join(tempDir, "Template.json"), "{}");
        lock (FactoryContainer.SharedLockObject)
        {
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                var settings = new Dictionary<string, string>
                {
                    ["Target:User"] = config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"] ?? "3306",
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1"
                };
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    settings[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(settings!).Build());

                var template = new Template { Name = "FractionalTemporal", FilePath = Path.Join(tempDir, "Template.json") };
                template.Tables.Add(new MySqlTable
                {
                    Name = TableName,
                    Columns =
                    {
                        new MySqlColumn { Name = "id", DataType = "INT", Nullable = false },
                        new MySqlColumn { Name = "happened_at", DataType = "DATETIME(6)", Nullable = false },
                        new MySqlColumn { Name = "maybe_at", DataType = "DATETIME(6)", Nullable = true, Default = "'2020-01-01 10:00:00.123456'" },
                        new MySqlColumn
                        {
                            Name = "touched_at", DataType = "TIMESTAMP(6)", Nullable = false,
                            Default = "CURRENT_TIMESTAMP(6)", OnUpdateCurrentTimestamp = "CURRENT_TIMESTAMP(6)"
                        },
                        new MySqlColumn { Name = "took", DataType = "TIME(3)", Nullable = true }
                    },
                    Indexes = { new Index { Name = "PRIMARY", PrimaryKey = true, Unique = true, IndexColumns = "id" } }
                });
                var product = new Product { Name = "FractionalTemporalProduct", Platform = Platform };
                var quench = new DatabaseQuench(product, template, new DatabaseQuenchOptions { Server = "127.0.0.1", DatabaseName = MainDb, SuppressKindling = true, DeliverData = false, Drops = new DropPolicy(Columns: true, ForeignKeys: true, CheckConstraints: true, ExcludeConstraints: true, Statistics: true, Indexes: true) }, new FileCheckpointManager(checkpointDir));
                quench.Execute();
                return quench.QuenchSuccessful;
            }
            finally
            {
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    private void Exec(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string Scalar(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : value.ToString();
    }
}
