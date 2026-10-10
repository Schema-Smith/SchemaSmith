// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Integration coverage for <c>Template.IdentificationDatabase</c> — re-targeting the
/// <c>DatabaseIdentificationScript</c> enumeration to a nominated control/registry database.
/// Uses the read-only preview path (<c>RunPreFlight(previewTargets: true)</c>), which drives the
/// real enumeration (<c>GetCommandForIdentification</c> → run the identification script) against
/// the live SQL Server container without deploying DDL.
///
/// FleetControlDbProduct carries two regular DB templates: FleetControlDb (IdentificationDatabase
/// set — reads a registry table that exists ONLY in the control DB) and FleetInitDb (no
/// IdentificationDatabase — reads sys.databases from the init DB, proving back-compat).
/// </summary>
[TestFixture]
[Category("SqlServer")]
[NonParallelizable]
public class FleetIdentificationDatabaseTests
{
    private const string ProductName = "FleetControlDbProduct";
    private const string ControlDb = "schemasmith_fleet_control_ss";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public FleetIdentificationDatabaseTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [SetUp]
    public void SetUp()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    [TearDown]
    public void TearDown()
    {
        DropControlDb();
        LogFactory.Clear();
        FactoryContainer.Unregister<IEnvironment>();
    }

    [Test]
    public void Enumeration_ReadsRegistryTableInControlDb()
    {
        // The registry table lives ONLY in the control DB. If IdentificationDatabase is honored,
        // enumeration connects there and reads the roster; otherwise the query fails against master.
        CreateControlDbWithRegistry(_mainDb);

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "FleetControlDb";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True, "Enumeration against the control DB must succeed.");
                Assert.That(pq.Failed, Is.False);
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains($"db: {_mainDb}")));
                _errorLog.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
            }
            finally
            {
                config["Target:Templates:0"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void Enumeration_WithoutIdentificationDatabase_UsesInitDb()
    {
        // Back-compat: FleetInitDb has no IdentificationDatabase, so its DatabaseIdentificationScript
        // runs against the init DB (master) — sys.databases — and still enumerates the roster.
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "FleetInitDb";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains($"db: {_mainDb}")));
                _errorLog.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
            }
            finally
            {
                config["Target:Templates:0"] = null;
                ClearTemplateTargets(config);
                ClearTargetFilters(config);
                config["SchemaPackagePath"] = null;
            }
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private static void ClearTargetFilters(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTargetFilters(config);

    private static void ClearTemplateTargets(IConfigurationRoot config) =>
        TemplateTargetsTestSupport.ClearTemplateTargets(config);

    private void CreateControlDbWithRegistry(string rosterDb)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"IF DB_ID('{ControlDb}') IS NULL CREATE DATABASE [{ControlDb}];";
        cmd.ExecuteNonQuery();
        conn.ChangeDatabase(ControlDb);
        cmd.CommandText = @"
IF OBJECT_ID('dbo.fleet_registry') IS NULL CREATE TABLE dbo.fleet_registry (db_name sysname NOT NULL);
DELETE FROM dbo.fleet_registry;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO dbo.fleet_registry (db_name) VALUES (@db);";
        var p = cmd.CreateParameter();
        p.ParameterName = "@db";
        p.Value = rosterDb;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropControlDb()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;
            // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            cmd.CommandText = $@"
IF DB_ID('{ControlDb}') IS NOT NULL
BEGIN
    ALTER DATABASE [{ControlDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{ControlDb}];
END";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }
}
