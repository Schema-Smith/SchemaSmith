// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
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
/// The SQL Server extraction routines say what they leave out through a WARNING message (a bound rule or default,
/// which is not modelled). SchemaTongs listens on its source connection and logs each one; without the listener the
/// message dies in the driver and the package is silently short of it.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
public class BoundRuleWarningTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private readonly List<string> _warnings = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"],
            ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"));
        _sourceDb = $"BoundRuleSrc_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        CreateSource();
        Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{_sourceDb}') IS NOT NULL BEGIN ALTER DATABASE [{_sourceDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_sourceDb}]; END";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void TheLog_CarriesTheEnginesWarning_ForEachBoundObject()
    {
        Assert.That(_warnings.Where(w => w.Contains("WARNING: Column [dbo].[Bnd].[Qty] has the bound ")).Select(w => w.Trim()).OrderBy(w => w).ToList(), Is.EqualTo(new[]
        {
            "WARNING: Column [dbo].[Bnd].[Qty] has the bound default [dbo].[ZeroDefault] (sp_bindefault), which is not modelled: the column is written without it.",
            "WARNING: Column [dbo].[Bnd].[Qty] has the bound rule [dbo].[PositiveRule] (sp_bindrule), which is not modelled: the column is written without it.",
        }), "warnings: " + string.Join(" | ", _warnings));
    }

    private IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(database);
        return conn;
    }

    private void Extract()
    {
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => _warnings.Add(ci.ArgAt<object>(0)?.ToString()));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(Substitute.For<IFile>());
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            foreach (var kind in new[] { "Schemas", "UserDefinedTypes", "Catalogs", "StopLists", "XMLSchemaCollections", "IndexedViews", "Synonyms", "Sequences", "Views", "Functions", "Procedures", "TableTriggers", "DDLTriggers", "ValidateScripts" })
                config[$"ShouldCast:{kind}"] = "false";
            config["ShouldCast:Tables"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform.SqlServer).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private void CreateSource()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        Run(cmd, $"CREATE DATABASE [{_sourceDb}]");
        conn.ChangeDatabase(_sourceDb);
        Run(cmd, "CREATE RULE dbo.PositiveRule AS @v > 0");
        Run(cmd, "CREATE DEFAULT dbo.ZeroDefault AS 0");
        Run(cmd, "CREATE TABLE dbo.Bnd ([Id] INT NOT NULL CONSTRAINT PK_Bnd PRIMARY KEY, [Qty] INT NULL)");
        Run(cmd, "EXEC sp_bindrule 'dbo.PositiveRule', 'dbo.Bnd.Qty'");
        Run(cmd, "EXEC sp_bindefault 'dbo.ZeroDefault', 'dbo.Bnd.Qty'");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
