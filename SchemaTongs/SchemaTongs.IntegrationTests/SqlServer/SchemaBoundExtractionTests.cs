// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
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
/// A SCHEMABINDING module is extracted into its own folder on the AfterTablesObjects slot — gap item I1,
/// slice 2, closing <see href="https://github.com/Schema-Smith/SchemaSmith/issues/323">#323</see>.
/// <para><b>Why placement is the whole feature.</b> SQL Server refuses a column change while a
/// schema-bound module references the column. The remedy is to drop the module, make the change, and let
/// the package recreate it — but SQL Server's ordinary Views and Functions folders run in the Objects
/// slot, BEFORE tables, so a module recreated from there would be put back before the column work rather
/// than after. Extraction therefore places schema-bound modules on the after-tables slot, and does so
/// regardless of whether the drop option is switched on, so a package is already shaped correctly the
/// day someone turns it on.</para>
/// <para><b>Asserted on the files a real extraction writes</b>, not by re-running a copy of the query — a
/// test holding its own copy passes happily while the real one drifts.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class SchemaBoundExtractionTests
{
    private const string ProductName = "SchemaBoundProduct";
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
        _db = $"TongsSb_{Guid.NewGuid():N}"[..30];

        Master($"CREATE DATABASE [{_db}]");

        OnDb(cmd =>
        {
            Run(cmd, "CREATE TABLE dbo.SbSource (Id INT NOT NULL PRIMARY KEY, Label VARCHAR(50) NOT NULL)");
            Run(cmd, "CREATE VIEW dbo.SbBoundView WITH SCHEMABINDING AS SELECT Id, Label FROM dbo.SbSource");
            Run(cmd, "CREATE VIEW dbo.SbPlainView AS SELECT Id, Label FROM dbo.SbSource");
            Run(cmd, "CREATE FUNCTION dbo.SbBoundFunc (@Id INT) RETURNS INT WITH SCHEMABINDING "
                     + "AS BEGIN RETURN (SELECT COUNT(*) FROM dbo.SbSource WHERE Id = @Id) END");
            Run(cmd, "CREATE FUNCTION dbo.SbPlainFunc (@Id INT) RETURNS INT AS BEGIN RETURN @Id END");
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            Master($"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Master($"DROP DATABASE IF EXISTS [{_db}]");
        }
        catch (DbException) { /* teardown must not mask an assertion */ }

        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void Master(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, sql);
    }

    private void OnDb(Action<IDbCommand> act)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        conn.ChangeDatabase(_db);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    private void Extract()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsSb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());

            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }
    }

    private string TemplateDir(string folder) =>
        Path.Join(_tempProductPath, "Templates", TemplateName, folder);

    private static string FilesIn(string dir) =>
        Directory.Exists(dir) ? string.Join(";", Directory.GetFiles(dir, "*.sql")) : "<folder absent>";

    [Test]
    public void ASchemaBoundModule_IsExtractedIntoTheAfterTablesFolder()
    {
        Extract();

        var boundViews = FilesIn(TemplateDir("SchemaBound Views"));
        var boundFuncs = FilesIn(TemplateDir("SchemaBound Functions"));

        Assert.Multiple(() =>
        {
            Assert.That(boundViews, Does.Contain("SbBoundView"),
                "a schema-bound view recreated from the ordinary Views folder would be put back BEFORE "
                + "the table work that required dropping it.\nSchemaBound Views: " + boundViews);
            Assert.That(boundFuncs, Does.Contain("SbBoundFunc"),
                "and the same applies to a schema-bound function.\nSchemaBound Functions: " + boundFuncs);
        });
    }

    [Test]
    public void AnUnboundModule_StaysInTheOrdinaryFolder()
    {
        // The negative half. Without it, a routing change that sent EVERY view to the schema-bound
        // folder would pass the assertions above while moving the whole package onto a slot it was never
        // meant to use -- and ordinary views would then deploy after tables for no reason.
        Extract();

        var plainViews = FilesIn(TemplateDir("Views"));
        var boundViews = FilesIn(TemplateDir("SchemaBound Views"));

        Assert.Multiple(() =>
        {
            Assert.That(plainViews, Does.Contain("SbPlainView"),
                "an ordinary view belongs where it always was.\nViews: " + plainViews);
            Assert.That(boundViews, Does.Not.Contain("SbPlainView"),
                "and must not be swept into the schema-bound folder.\nSchemaBound Views: " + boundViews);
            Assert.That(FilesIn(TemplateDir("Functions")), Does.Contain("SbPlainFunc"),
                "same for an ordinary function");
        });
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
            ["ShouldCast:Views"] = "true",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "true",
            ["ShouldCast:UserDefinedTypes"] = "false",
            ["ShouldCast:XmlSchemaCollections"] = "false",
            ["ShouldCast:FullTextCatalogs"] = "false",
            ["ShouldCast:FullTextStopLists"] = "false",
        };
        // Both halves need them: extraction reads Source:*, the redeploy reads Target:*, and a missing
        // TrustServerCertificate surfaces only as "Error validating configured servers".
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
