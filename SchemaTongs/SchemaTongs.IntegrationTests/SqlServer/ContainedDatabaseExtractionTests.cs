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
/// A partially contained database keeps its catalog in a fixed collation and its temp tables in its own, so every
/// comparison of the two failed and nothing could be extracted from one.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class ContainedDatabaseExtractionTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private int _containmentWas;
    private readonly List<string> _errors = [];
    private readonly List<string> _written = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"],
            ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"));
        _sourceDb = $"ContainedSrc_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        CreateSource();
        Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        Run(cmd, $"IF DB_ID('{_sourceDb}') IS NOT NULL BEGIN ALTER DATABASE [{_sourceDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_sourceDb}]; END");
        // Left on: fixtures in other shard processes create contained databases too, and switching it off under them fails both.
    }

    [Test]
    public void EveryObject_IsExtracted_WithNoError()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_errors, Is.Empty, "errors: " + string.Join(" | ", _errors));
            foreach (var expected in new[] { "dbo.Child.json", "dbo.Parent.json", "dbo.v_child.sql", "dbo.p_child.sql", "dbo.f_twice.sql", "dbo.Child.tr_child.sql" })
                Assert.That(_written, Has.Some.EndsWith(expected), $"{expected} was written; written: " + string.Join(", ", _written.Select(System.IO.Path.GetFileName)));
        });
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
        var errorLog = Substitute.For<ILog>();
        progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => _errors.Add(ci.ArgAt<object>(0)?.ToString()));
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => _errors.Add(ci.ArgAt<object>(0)?.ToString()));
        var file = Substitute.For<IFile>();
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci => _written.Add(ci.ArgAt<string>(0)));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["ShouldCast:ValidateScripts"] = "false";
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
        cmd.CommandText = "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE [name] = 'contained database authentication'";
        _containmentWas = Convert.ToInt32(cmd.ExecuteScalar());
        if (_containmentWas == 0) Run(cmd, "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");
        Run(cmd, $"CREATE DATABASE [{_sourceDb}] CONTAINMENT = PARTIAL COLLATE Japanese_CS_AS_KS");
        conn.ChangeDatabase(_sourceDb);
        Run(cmd, "CREATE TABLE dbo.Parent ([Id] INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY, [Code] NVARCHAR(20) NOT NULL CONSTRAINT UX_Parent_Code UNIQUE)");
        Run(cmd, "CREATE TABLE dbo.Child ([Id] INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, [ParentId] INT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent ([Id]), " +
                 "[Qty] INT NOT NULL CONSTRAINT DF_Child_Qty DEFAULT 0 CONSTRAINT CK_Child_Qty CHECK ([Qty] >= 0), [Label] NVARCHAR(40) NULL, [Twice] AS ([Qty] * 2))");
        Run(cmd, "CREATE INDEX IX_Child_Positive ON dbo.Child ([Qty]) INCLUDE ([Label]) WHERE [Qty] > 0");
        Run(cmd, "CREATE STATISTICS ST_Child_Label ON dbo.Child ([Label])");
        Run(cmd, "EXEC sp_addextendedproperty 'MS_Description', N'What the row is', 'SCHEMA', 'dbo', 'TABLE', 'Child', 'COLUMN', 'Label'");
        Run(cmd, "CREATE VIEW dbo.v_child AS SELECT [Id], [Qty] FROM dbo.Child");
        Run(cmd, "CREATE FUNCTION dbo.f_twice (@v INT) RETURNS INT AS BEGIN RETURN @v * 2 END");
        Run(cmd, "CREATE PROCEDURE dbo.p_child AS SELECT [Id] FROM dbo.Child");
        Run(cmd, "CREATE TRIGGER dbo.tr_child ON dbo.Child AFTER INSERT AS SET NOCOUNT ON");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }
}
