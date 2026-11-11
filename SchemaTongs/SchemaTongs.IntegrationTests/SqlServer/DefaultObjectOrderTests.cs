// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// A new extraction with no <c>Product:ObjectOrder</c> writes columns in their physical order. Alphabetical order made a
/// fresh deploy of the package reorder the columns, which breaks positional INSERTs.
/// <para>Uses its own database: extraction reads every table in the source.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class DefaultObjectOrderTests
{
    private const string TemplateName = "Main";

    private string _db = "";
    private string _masterConnectionString = "";
    private string _tempProductPath;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = $"TongsOrder_{Guid.NewGuid():N}"[..30];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE DATABASE [{_db}]";
        cmd.ExecuteNonQuery();
        conn.ChangeDatabase(_db);
        cmd.CommandText = "CREATE TABLE dbo.Ordered (Zeta INT NOT NULL PRIMARY KEY, Alpha NVARCHAR(10) NULL, Middle NVARCHAR(10) NULL)";
        cmd.ExecuteNonQuery();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _db);
        }
        catch (DbException) { /* teardown must not mask an assertion */ }
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch (IOException) { /* best effort - temp cleanup */ }
        }
    }

    [Test]
    public void AnExtractionWithNoObjectOrder_WritesColumnsInPhysicalOrder()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsOrderSs_{Guid.NewGuid():N}");

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
                new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();

                var file = Directory.GetFiles(Path.Join(_tempProductPath, "Templates", TemplateName, "Tables"), "*Ordered*.json").Single();
                var table = JsonHelper.TableLoad(file, Platform.SqlServer);
                Assert.That(table.Columns.Select(c => c.Name.Trim('[', ']')), Is.EqualTo(new[] { "Zeta", "Alpha", "Middle" }),
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
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root["SqlServer:Server"],
            ["Source:Port"] = root["SqlServer:Port"],
            ["Source:User"] = root["SqlServer:User"],
            ["Source:Password"] = root["SqlServer:Password"],
            ["Source:Database"] = _db,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = "DefaultOrderProductSs",
            ["Template:Name"] = TemplateName,
            ["ShouldCast:ValidateScripts"] = "false"
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
