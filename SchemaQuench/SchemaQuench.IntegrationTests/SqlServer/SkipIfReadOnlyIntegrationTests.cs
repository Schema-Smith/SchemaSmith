// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Verifies <c>Template.SkipIfReadOnly</c> end to end against a genuinely read-only database.
/// The motivating case is an Availability Group readable secondary: the template must still
/// resolve the target (so it validates and satisfies RequireAtLeastOneTarget) but must not
/// attempt to apply anything there.
/// <para>The read-write control run is what makes the read-only assertion meaningful — without
/// it, "the table was not created" would also pass for a product that simply does not work.</para>
/// </summary>
[Category("SqlServer")]
public class SkipIfReadOnlyIntegrationTests
{
    private const string ProductName = "SkipIfReadOnly";
    private const string ProbeDb = "SkipIfReadOnlyProbe";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;

    public SkipIfReadOnlyIntegrationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    [OneTimeSetUp]
    public void CreateProbeDatabase()
    {
        // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        ExecuteOnMaster($@"
IF DB_ID('{ProbeDb}') IS NOT NULL
BEGIN
    ALTER DATABASE [{ProbeDb}] SET READ_WRITE WITH ROLLBACK IMMEDIATE;
    ALTER DATABASE [{ProbeDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{ProbeDb}];
END;
CREATE DATABASE [{ProbeDb}];");
    }

    [OneTimeTearDown]
    public void DropProbeDatabase()
    {
        // Pooled connections opened against the probe keep it "in use" and block the drop.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        ExecuteOnMaster($@"
IF DB_ID('{ProbeDb}') IS NOT NULL
BEGIN
    ALTER DATABASE [{ProbeDb}] SET READ_WRITE WITH ROLLBACK IMMEDIATE;
    ALTER DATABASE [{ProbeDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{ProbeDb}];
END;");
    }

    [Test]
    public void ReadOnlyTarget_TemplateIsSkipped_RunSucceedsAndNoDdlApplied()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            SetProbeReadOnly(true);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                // A skip is a success, not a failure: the run must not exit non-zero.
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                _progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains("read-only") && s.Contains("SkipIfReadOnly")));

                Assert.That(TableExists(), Is.False,
                    "no DDL may be applied to a read-only target when SkipIfReadOnly is set");
            }
            finally
            {
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void WritableTarget_SameProduct_IsNotSkipped_AndAppliesDdl()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            SetProbeReadOnly(false);
            DropProbeTable();

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                Assert.That(TableExists(), Is.True,
                    "the same product must deploy normally once the target is writable — otherwise the " +
                    "read-only assertion above proves nothing");
            }
            finally
            {
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = null;
            }
        }
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main([]);

    private void SetProbeReadOnly(bool readOnly) =>
        ExecuteOnMaster($"ALTER DATABASE [{ProbeDb}] SET {(readOnly ? "READ_ONLY" : "READ_WRITE")} WITH ROLLBACK IMMEDIATE;");

    private void DropProbeTable() =>
        ExecuteOnProbe("IF OBJECT_ID('dbo.ShouldNotBeCreated', 'U') IS NOT NULL DROP TABLE dbo.ShouldNotBeCreated;");

    private bool TableExists()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(ProbeDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID('dbo.ShouldNotBeCreated', 'U') IS NULL THEN 0 ELSE 1 END";
        var result = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        conn.Close();
        return result;
    }

    private void ExecuteOnMaster(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void ExecuteOnProbe(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(ProbeDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
