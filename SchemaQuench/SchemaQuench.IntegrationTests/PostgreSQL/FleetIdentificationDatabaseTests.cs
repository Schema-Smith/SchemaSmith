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

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Integration coverage for <c>Template.IdentificationDatabase</c> on PostgreSQL — the engine that
/// could NOT read a registry table at enumeration time before this feature (a PG connection is
/// bound to one database, no cross-database query). Uses the read-only preview path
/// (<c>RunPreFlight(previewTargets: true)</c>), which drives the real enumeration
/// (<c>GetCommandForIdentification</c> → run the identification script) against the live container.
///
/// FleetControlDbProduct carries two regular DB templates: FleetControlDb (IdentificationDatabase
/// set — reads a registry table that exists ONLY in the control DB) and FleetInitDb (no
/// IdentificationDatabase — reads pg_database from the init DB, proving back-compat).
/// </summary>
[TestFixture]
[Category("PostgreSQL")]
[NonParallelizable]
public class FleetIdentificationDatabaseTests
{
    private const string ProductName = "FleetControlDbProduct";
    private const string ControlDb = "schemasmith_fleet_control_pg";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public FleetIdentificationDatabaseTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
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
        // The registry table lives ONLY in the control DB. On PostgreSQL there is NO other way to
        // read it at enumeration time — success proves the enumeration connection targeted the control DB.
        CreateControlDbWithRegistry(_mainDb);

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
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
        // runs against the init DB (postgres) — pg_database — and still enumerates the roster.
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", ProductName);
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
        DropControlDb();
        using (var admin = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
        {
            admin.Open();
            using var acmd = admin.CreateCommand();
            acmd.CommandText = $"CREATE DATABASE \"{ControlDb}\"";
            acmd.ExecuteNonQuery();
            admin.Close();
        }

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(ControlDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE public.fleet_registry (db_name text NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO public.fleet_registry (db_name) VALUES (@db)";
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
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $@"
SELECT pg_terminate_backend(pid)
  FROM pg_stat_activity
  WHERE datname = '{ControlDb}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{ControlDb}\"";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch (System.Data.Common.DbException)
        {
            // Best-effort cleanup.
        }
    }
}
