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

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration coverage for <c>Template.IdentificationDatabase</c> on MySQL — re-targeting the
/// <c>DatabaseIdentificationScript</c> enumeration to a nominated control/registry database. Uses
/// the read-only preview path (<c>RunPreFlight(previewTargets: true)</c>), which drives the real
/// enumeration (<c>GetCommandForIdentification</c> → run the identification script) against the
/// live MySQL container.
///
/// FleetControlDbProduct carries two regular DB templates: FleetControlDb (IdentificationDatabase
/// set — reads a registry table reachable only when connected to the control DB) and FleetInitDb
/// (no IdentificationDatabase — reads information_schema.SCHEMATA from the init DB, back-compat).
/// </summary>
public abstract class FleetIdentificationDatabaseTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ProductPlatformFolder { get; }

    private const string ProductName = "FleetControlDbProduct";
    private const string ControlDb = "schemasmith_fleet_control_my";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    protected FleetIdentificationDatabaseTestsSharedTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform, config["Target:Server"], "information_schema",
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
        // The unqualified registry read (SELECT db_name FROM fleet_registry) resolves only when the
        // enumeration connection's default database IS the control DB — success proves the retarget.
        CreateControlDbWithRegistry(_mainDb);

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "FleetControlDb";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True, "Enumeration against the control DB must succeed.");
                Assert.That(pq.Failed, Is.False);
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains($"db: {_mainDb}", StringComparison.OrdinalIgnoreCase)));
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
        // runs against the init DB (information_schema) and still enumerates the roster.
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, ProductName);
            ClearTargetFilters(config);
            ClearTemplateTargets(config);
            config["Target:Templates:0"] = "FleetInitDb";

            try
            {
                var pq = new ProductQuench();
                var result = pq.RunPreFlight(previewTargets: true);

                Assert.That(result, Is.True);
                Assert.That(pq.Failed, Is.False);
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains($"db: {_mainDb}", StringComparison.OrdinalIgnoreCase)));
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
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{ControlDb}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS `{ControlDb}`.fleet_registry (db_name VARCHAR(128) NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DELETE FROM `{ControlDb}`.fleet_registry";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO `{ControlDb}`.fleet_registry (db_name) VALUES (@db)";
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
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{ControlDb}`";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }
}
