// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// A new extraction with no <c>Product:ObjectOrder</c> writes columns in their physical order. Alphabetical order made a
/// fresh deploy of the package reorder the columns, which breaks positional INSERTs.
/// <para>Uses its own database: extraction reads every table in the source.</para>
/// </summary>
[Category("PostgreSQL")]
public class DefaultObjectOrderTests
{
    private const string TemplateName = "Main";
    private const string ProductName = "DefaultOrderProduct";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres",
            config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = $"tongs_order_{Guid.NewGuid():N}"[..30];

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();
        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
        cmd.CommandText = "CREATE TABLE public.ordered (zeta INT4 NOT NULL PRIMARY KEY, alpha TEXT NULL, middle TEXT NULL);";
        cmd.ExecuteNonQuery();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_integrationDb}\";";
            cmd.ExecuteNonQuery();
        }
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch (IOException) { /* best effort - temp cleanup */ }
        }
    }

    [Test]
    public void AnExtractionWithNoObjectOrder_WritesColumnsInPhysicalOrder()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsOrder_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            try
            {
                new SchemaTongs(Platform.PostgreSQL).CastTemplate();

                var file = Directory.GetFiles(Path.Join(_tempProductPath, "Templates", TemplateName, "Tables"), "*ordered*.json").Single();
                var table = JsonHelper.TableLoad(file, Platform.PostgreSQL);
                Assert.That(table.Columns.Select(c => c.Name.Trim('"')), Is.EqualTo(new[] { "zeta", "alpha", "middle" }),
                    "with no ObjectOrder the columns must keep the table's physical order");
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    private IConfigurationRoot BuildConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "PostgreSQL:ConnectionProperties");
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["PostgreSQL:Server"],
            ["Source:Port"] = rootConfig["PostgreSQL:Port"],
            ["Source:User"] = rootConfig["PostgreSQL:User"],
            ["Source:Password"] = rootConfig["PostgreSQL:Password"],
            ["Source:Database"] = _integrationDb,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:ValidateScripts"] = "false"
        };
        foreach (var prop in connProps)
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
