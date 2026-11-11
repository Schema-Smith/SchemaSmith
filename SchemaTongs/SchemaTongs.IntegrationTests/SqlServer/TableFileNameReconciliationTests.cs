// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
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
using Schema.Validation;
using Schema.Validation.Checks;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// A package SchemaTongs just wrote passes its own <c>--Validate</c> table-file-name check (<c>SS-FILE-NAME-003</c>):
/// each table file is named from the schema its content carries, for the default schema and a named one alike.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class TableFileNameReconciliationTests
{
    private const string ProductName = "ReconProductSs";
    private const string TemplateName = "Main";

    private string _db = "";
    private string _masterConnectionString = "";
    private string _tempProductPath = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = $"TongsFileName_{Guid.NewGuid():N}"[..30];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"CREATE DATABASE [{_db}]");
        conn.ChangeDatabase(_db);
        Run(cmd, "CREATE TABLE dbo.Widget (WidgetId INT NOT NULL CONSTRAINT PK_Widget PRIMARY KEY, Label NVARCHAR(50) NULL)");
        Run(cmd, "EXEC('CREATE SCHEMA sales')");
        Run(cmd, "CREATE TABLE sales.Invoice (InvoiceId INT NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY, Total DECIMAL(10, 2) NOT NULL)");
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
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void Extraction_NamesTableFilesFromContentSchema_PassesTableFileNameCheck()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"SchemaTongsFileNameSs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);

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
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }

        var tablesDir = Path.Join(_tempProductPath, "Templates", TemplateName, "Tables");
        var files = Directory.Exists(tablesDir) ? Directory.GetFiles(tablesDir, "*.json").Select(Path.GetFileName).ToList() : [];
        // Without both files the check below would pass over nothing.
        Assert.That(files, Has.Count.EqualTo(2), "both tables were extracted: " + string.Join(", ", files));

        var ctx = new ValidationContext(new Product { Name = ProductName, Platform = Platform.SqlServer }, new List<Template>(), _tempProductPath);
        var findings = new TableFileNameCheck().Run(ctx).Where(f => f.Code == "SS-FILE-NAME-003").ToList();
        Assert.That(findings, Is.Empty, "SchemaTongs' own extraction output must not trip SS-FILE-NAME-003: "
            + string.Join("; ", findings.Select(f => f.Message)) + " (files: " + string.Join(", ", files) + ")");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
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
            ["Target:Server"] = root["SqlServer:Server"],
            ["Target:Port"] = root["SqlServer:Port"],
            ["Target:User"] = root["SqlServer:User"],
            ["Target:Password"] = root["SqlServer:Password"],
            ["ScriptTokens:MainDB"] = _db,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "false",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:UserDefinedTypes"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:Schemas"] = "false",
            ["ShouldCast:IndexedViews"] = "false",
            ["ShouldCast:Sequences"] = "false",
            ["ShouldCast:Synonyms"] = "false",
            ["ShouldCast:XmlSchemaCollections"] = "false",
            ["ShouldCast:FullTextCatalogs"] = "false",
            ["ShouldCast:FullTextStopLists"] = "false",
            ["ShouldCast:SecurityPolicies"] = "false",
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
