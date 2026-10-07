// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.GenuineOldBinary;

/// <summary>
/// A full extraction, every object type on, from whatever SQL Server the SmithySettings_SqlServer__* variables name.
/// On SQL Server 2008 R2 it used to stop at the sequence step (sys.sequences is 2012) and exit 3 with half a package,
/// so the synonym step after it never ran.
/// <para>[Explicit]: its value is on the local old binaries, which CI does not have. Run per instance:
///   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 SmithySettings_SqlServer__User=sa
///   SmithySettings_SqlServer__Password='SchemaSmith!Old2026' SmithySettings_SqlServer__ConnectionProperties__TrustServerCertificate=true
///   dotnet test SchemaTongs/SchemaTongs.IntegrationTests --filter FullyQualifiedName~GenuineOldBinary</para>
/// </summary>
[Explicit("Run against a genuine old SQL Server instance via the SmithySettings_SqlServer__* variables.")]
[TestFixture]
public class OldBinaryExtractionTests
{
    private string _db = "";
    private string _masterConnectionString = "";
    private string _productPath = "";
    private int _serverMajor;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = "OldBinExtract_" + Guid.NewGuid().ToString("N")[..12];
        _productPath = Path.Join(Path.GetTempPath(), _db);

        using var conn = Open("master");
        _serverMajor = Convert.ToInt32(Scalar(conn, "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)), 4) AS INT)"));
        Exec(conn, $"CREATE DATABASE [{_db}]");
        conn.ChangeDatabase(_db);
        Exec(conn, "CREATE TABLE dbo.Customer (Id INT NOT NULL CONSTRAINT PK_Customer PRIMARY KEY, Name NVARCHAR(100) NOT NULL)");
        Exec(conn, "CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer");
        Exec(conn, "CREATE SYNONYM dbo.Cust FOR dbo.Customer");
        if (_serverMajor >= 11) Exec(conn, "CREATE SEQUENCE dbo.OrderNo AS INT START WITH 100");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var conn = Open("master");
            Exec(conn, $"IF DB_ID('{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END");
        }
        catch (System.Data.Common.DbException) { /* best-effort cleanup */ }
        try { if (Directory.Exists(_productPath)) Directory.Delete(_productPath, true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    [Test]
    public void EveryObjectTypeExtracts_AndSequencesOnlyWhereTheServerHasThem()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            try
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
                FactoryContainer.Register(Substitute.For<IEnvironment>());
                LogFactory.Register("ErrorLog", Substitute.For<ILog>());
                LogFactory.Register("ProgressLog", Substitute.For<ILog>());

                new SchemaTongs(Platform.SqlServer).CastTemplate();
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }

        var files = Directory.GetFiles(_productPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_productPath, f).Replace(Path.DirectorySeparatorChar, '/')).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Some.EndsWith("Tables/dbo.Customer.json"));
            Assert.That(files, Has.Some.EndsWith("Views/dbo.vCustomer.sql"));
            Assert.That(files, Has.Some.EndsWith("Synonyms/dbo.Cust.sql"), "the step after sequences must run");
            Assert.That(files.Any(f => f.EndsWith("Sequences/dbo.OrderNo.sql")), Is.EqualTo(_serverMajor >= 11),
                $"sequences are extracted from SQL Server 2012 (major 11); this server is major {_serverMajor}");
        });
    }

    private IConfigurationRoot BuildConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["SqlServer:Server"],
            ["Source:Port"] = rootConfig["SqlServer:Port"],
            ["Source:User"] = rootConfig["SqlServer:User"],
            ["Source:Password"] = rootConfig["SqlServer:Password"],
            ["Source:Database"] = _db,
            ["Product:Path"] = _productPath,
            ["Product:Name"] = "OldBinExtract",
            ["Template:Name"] = "Main"
        };
        foreach (var prop in ConnectionString.ReadProperties(rootConfig, "SqlServer:ConnectionProperties"))
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        if (database != "master") conn.ChangeDatabase(database);
        return conn;
    }

    private static void Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
