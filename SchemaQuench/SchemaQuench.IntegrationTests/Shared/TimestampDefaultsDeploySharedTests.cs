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
/// With <c>explicit_defaults_for_timestamp</c> OFF (the default on MySQL 5.7 and on MariaDB before 10.10) the engine invents
/// defaults for a <c>TIMESTAMP NOT NULL</c> column declared without one. The first column silently got
/// <c>DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP</c>; later ones got a zero date, which strict mode refuses. So the
/// deploy failed on MySQL, and on MariaDB every redeploy re-altered both columns. The deploy now sets the variable ON for its
/// table work, so the columns are created as declared. The server's global is switched OFF for the fixture, because the
/// deploy opens its own connections and those take the global value.
/// </summary>
[NonParallelizable]
public abstract class TimestampDefaultsDeploySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string ConfigPrefix { get; }

    private const string TableName = "ts_defaults_deploy";
    private IDbConnection _connection = null!;
    private string _originalGlobal;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        try
        {
            Exec("SET SESSION explicit_defaults_for_timestamp = 1");
        }
        catch (System.Data.Common.DbException)
        {
            Assert.Ignore("explicit_defaults_for_timestamp cannot be set per session on this server, so a deploy cannot pin it.");
        }
        _originalGlobal = Scalar("SELECT @@GLOBAL.explicit_defaults_for_timestamp");
        Exec("SET GLOBAL explicit_defaults_for_timestamp = 0");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            if (_originalGlobal != null)
                Exec($"SET GLOBAL explicit_defaults_for_timestamp = {(_originalGlobal is "1" or "ON" ? 1 : 0)}");
            Exec($"DROP TABLE IF EXISTS `{MainDb}`.`{TableName}`");
        }
        finally
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    [Test]
    public void TimestampNotNullWithoutDefault_DeploysAsDeclared_AndRedeploysWithoutChange()
    {
        Exec($"DROP TABLE IF EXISTS `{MainDb}`.`{TableName}`");
        Exec($"DELETE FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");

        Assert.That(Deploy(), Is.True, "the first deploy");
        Exec($"DELETE FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");
        Assert.That(Deploy(), Is.True, "an unchanged redeploy");

        Assert.Multiple(() =>
        {
            Assert.That(ColumnShape("created"), Is.EqualTo("NULL|"), "no default or ON UPDATE invented for the first column");
            Assert.That(ColumnShape("changed"), Is.EqualTo("NULL|"), "no zero-date default invented for the second column");
            Assert.That(Scalar($"SELECT COUNT(*) FROM `{MainDb}`.SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{TableName}%'"),
                Is.EqualTo("0"), "an unchanged redeploy changes nothing");
            Assert.That(Scalar("SELECT @@GLOBAL.explicit_defaults_for_timestamp"), Is.AnyOf("0", "OFF"),
                "premise: the server still invents defaults for new sessions");
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
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Target:User"] = config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"] ?? "3306",
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1"
                }!).Build());

                var template = new Template { Name = "TimestampDefaults", FilePath = Path.Join(tempDir, "Template.json") };
                template.Tables.Add(new MySqlTable
                {
                    Name = TableName,
                    Columns =
                    {
                        new MySqlColumn { Name = "id", DataType = "INT", Nullable = false },
                        new MySqlColumn { Name = "created", DataType = "TIMESTAMP", Nullable = false },
                        new MySqlColumn { Name = "changed", DataType = "TIMESTAMP", Nullable = false }
                    },
                    Indexes = { new Index { Name = "PRIMARY", PrimaryKey = true, Unique = true, IndexColumns = "id" } }
                });
                var product = new Product { Name = "TimestampDefaultsProduct", Platform = Platform };
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
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    private string ColumnShape(string column) =>
        Scalar($"SELECT CONCAT(COALESCE(COLUMN_DEFAULT, 'NULL'), '|', EXTRA) FROM information_schema.COLUMNS " +
               $"WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = '{column}'");

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
