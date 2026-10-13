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
using System.Collections.Generic;
using System.Linq;
using System.Data;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Slice-4 CreateSchemaIfMissing integration tests (design §10.4).
///
/// <para>Uses SchemaTemplateProduct with test-setup variants rather than a dedicated fixture.
/// The discovery script reads dbo.SchemaTemplateTenants (or dbo.SchemaTemplateCreateSchemaTenants
/// for the CreateSchema variant); for "missing schema" tests we insert a tenant row without
/// pre-creating the schema, so the engine sees a schema name returned by discovery that does not
/// exist as a real SQL Server schema. That exercises the CreateSchemaIfMissing path without
/// altering the fixture at all.</para>
/// </summary>
[Category("SqlServer")]
public class SchemaCreationTests
{
    private const string ProductName = "SchemaTemplateProduct";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public SchemaCreationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    /// <summary>
    /// CreateSchemaIfMissing: false (default) + discovery returns a schema that does not exist
    /// as an actual SQL Server schema. The engine must fail that work unit with a clear error
    /// message referencing the three onboarding paths. Other tenants whose schemas DO exist
    /// must complete (governed by ContinueOnSchemaFailure=true default).
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_DefaultFalse_MissingSchema_FailsWithClearError_OthersComplete()
    {
        const string missingTenant = "tenant_ghost";
        var existingTenants = new[] { "tenant_acme", "tenant_beta" };
        var allTenants = new List<string>(existingTenants) { missingTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            // Insert the ghost tenant into the discovery table but do NOT pre-create its schema.
            ResetTrackingAndCreateTenantSchemas(existingTenants);
            // Add ghost tenant row only — no schema creation.
            ExecuteOnMainDb($"INSERT INTO dbo.SchemaTemplateTenants ([Name]) VALUES (N'{missingTenant}');");

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", ProductName);

            try
            {
                RunSchemaQuench();

                // Exit code must be non-zero (one work unit failed).
                _environment.Received().Exit(2);

                // Error message must mention the missing schema and the three onboarding paths.
                _progressLog.Received().Error(Arg.Is<string>(msg =>
                    msg.Contains($"Schema '{missingTenant}'") &&
                    msg.Contains($"'{_mainDb}'") &&
                    msg.Contains("CreateSchemaIfMissing") &&
                    msg.Contains("Pre-create") &&
                    msg.Contains("Shared-template helper")));

                // The two existing tenants must complete successfully.
                foreach (var tenant in existingTenants)
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // The ghost tenant must NOT have been quenched.
                _progressLog.DidNotReceive().Info($"[{_server}].[{_mainDb}] [Schema: {missingTenant}] Successfully Quenched");

                // The ghost schema must not have been created.
                Assert.That(SchemaExists(missingTenant), Is.False,
                    "The ghost schema must not be created when CreateSchemaIfMissing is false.");
            }
            finally
            {
                DropTenantSchemas(allTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// CreateSchemaIfMissing: true + discovery returns a schema that does not exist.
    /// The engine must emit CREATE SCHEMA, proceed with the rest of the iteration, and
    /// the schema must exist after the quench.
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_True_MissingSchema_CreatesSchemaAndProceeds()
    {
        const string autoTenant = "tenant_auto";
        var existingTenants = new[] { "tenant_acme" };
        var allTenants = new List<string>(existingTenants) { autoTenant };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            // Pre-create existing tenants normally.
            ResetTrackingAndCreateTenantSchemas(existingTenants);
            // Insert auto tenant row — schema does not exist yet.
            ExecuteOnMainDb($"INSERT INTO dbo.SchemaTemplateCreateSchemaTenants ([Name]) VALUES (N'{autoTenant}');");

            // SchemaTemplateCreateSchemaProduct is SchemaTemplateProduct with CreateSchemaIfMissing:true.
            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "SchemaTemplateCreateSchemaProduct");

            try
            {
                RunSchemaQuench();

                // No errors; exit with success code (0).
                _environment.DidNotReceive().Exit(2);
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Engine logged CREATE SCHEMA via the unified SchemaProvisioner path.
                _progressLog.Received().Info(Arg.Is<string>(msg =>
                    msg.Contains($"Creating schema [{autoTenant}]") && msg.Contains("(CreateIfMissing: true)")));

                // The auto tenant was quenched.
                _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {autoTenant}] Successfully Quenched");

                // The schema now exists.
                Assert.That(SchemaExists(autoTenant), Is.True,
                    "Schema must be created when CreateSchemaIfMissing=true and schema was missing.");
                Assert.That(SummaryRecords("schema", autoTenant, "created"), Is.True,
                    "the deployment summary must record the schema it created");
            }
            finally
            {
                DropTenantSchemas(allTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// Schema already exists + CreateSchemaIfMissing: true → engine skips creation and proceeds
    /// without error. CREATE SCHEMA must NOT be logged.
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_True_SchemaAlreadyExists_SkipsCreation()
    {
        var existingTenants = new[] { "tenant_acme", "tenant_beta" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(existingTenants);

            FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] =
                TestHelper.GetTestProductPath("SqlServer", "SchemaTemplateCreateSchemaProduct");

            try
            {
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // CREATE SCHEMA must NOT have been emitted for pre-existing schemas.
                _progressLog.DidNotReceive().Info(Arg.Is<string>(msg =>
                    msg.Contains("Creating schema") && msg.Contains("(CreateIfMissing: true)")));

                foreach (var tenant in existingTenants)
                    _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");
            }
            finally
            {
                DropTenantSchemas(existingTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    /// <summary>
    /// WhatIf + CreateSchemaIfMissing: true + missing schema → the engine must render the
    /// "Would create schema" log line and NOT issue real CREATE SCHEMA DDL. Prior to the fix,
    /// the pre-existing CreateSchemaIfMissing path executed CREATE SCHEMA unconditionally —
    /// WhatIf was silently broken for the legacy onboarding path.
    /// </summary>
    [Test]
    public void CreateSchemaIfMissing_True_WhatIf_MissingSchema_LogsWouldCreateAndDoesNotExecute()
    {
        const string ghostTenant = "tenant_whatif_ghost";
        var existingTenants = new[] { "tenant_acme" };

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(existingTenants);
            ExecuteOnMainDb($"INSERT INTO dbo.SchemaTemplateCreateSchemaTenants ([Name]) VALUES (N'{ghostTenant}');");

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "SchemaTemplateCreateSchemaProduct");
            config["WhatIfONLY"] = "true";

            try
            {
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // WhatIf log line surfaces via the unified SchemaProvisioner WhatIf branch.
                _progressLog.Received().Info(Arg.Is<string>(msg =>
                    msg.Contains("[WhatIf]") && msg.Contains($"Would create schema [{ghostTenant}]")
                    && msg.Contains("(CreateIfMissing: true)")));

                // Real CREATE was NOT logged.
                _progressLog.DidNotReceive().Info(Arg.Is<string>(msg =>
                    msg.Contains("Creating schema") && !msg.Contains("[WhatIf]")
                    && msg.Contains("(CreateIfMissing: true)")));

                // The schema does NOT exist on the server — WhatIf must not have applied DDL.
                Assert.That(SchemaExists(ghostTenant), Is.False,
                    "WhatIf + CreateSchemaIfMissing: true must NOT actually create the schema.");
                Assert.That(SummaryRecords("schema", ghostTenant, "wouldCreate"), Is.True,
                    "the WhatIf summary must record the schema it would create");
            }
            finally
            {
                config["WhatIfONLY"] = null;
                DropTenantSchemas(new List<string>(existingTenants) { ghostTenant });
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private static bool SummaryRecords(string objectType, string objectName, string action)
    {
        var summary = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Join(Schema.Utility.ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
        return (summary.SelectToken("objectChanges.details") as Newtonsoft.Json.Linq.JArray ?? [])
            .Any(d => (string)d["objectType"] == objectType && (string)d["objectName"] == objectName && (string)d["action"] == action);
    }

    private static void ClearCheckpointsForProduct(string productName)
    {
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(productName);
    }

    private void ResetTrackingAndCreateTenantSchemas(IEnumerable<string> tenants)
    {
        ClearCheckpointsForProduct(ProductName);
        ClearCheckpointsForProduct("SchemaTemplateCreateSchemaProduct");
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NOT NULL DELETE FROM dbo.SchemaTemplateTenants;
IF OBJECT_ID('dbo.SchemaTemplateCreateSchemaTenants', 'U') IS NOT NULL DELETE FROM dbo.SchemaTemplateCreateSchemaTenants;
IF OBJECT_ID('dbo.Lookup', 'U') IS NOT NULL DELETE FROM dbo.Lookup;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, new[] { "tenant_acme", "tenant_beta", "tenant_globex", "tenant_ghost", "tenant_auto" });

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }

        // Seed BOTH renamed tenant tables — tests in this fixture exercise both
        // SchemaTemplateProduct (reads dbo.SchemaTemplateTenants) and
        // SchemaTemplateCreateSchemaProduct (reads dbo.SchemaTemplateCreateSchemaTenants).
        cmd.CommandText = @"
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NULL
    CREATE TABLE dbo.SchemaTemplateTenants ([Name] NVARCHAR(128) NOT NULL CONSTRAINT PK_SchemaTemplateTenants PRIMARY KEY);
IF OBJECT_ID('dbo.SchemaTemplateCreateSchemaTenants', 'U') IS NULL
    CREATE TABLE dbo.SchemaTemplateCreateSchemaTenants ([Name] NVARCHAR(128) NOT NULL CONSTRAINT PK_SchemaTemplateCreateSchemaTenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO dbo.SchemaTemplateTenants ([Name]) VALUES (N'{tenant}');";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"INSERT INTO dbo.SchemaTemplateCreateSchemaTenants ([Name]) VALUES (N'{tenant}');";
            cmd.ExecuteNonQuery();
        }

        conn.Close();
    }

    private void DropTenantSchemas(IEnumerable<string> tenants)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        DropTenantSchemasInternal(cmd, tenants);

        cmd.CommandText = @$"
IF OBJECT_ID('dbo.SharedAudit', 'U') IS NOT NULL DROP TABLE dbo.SharedAudit;
IF OBJECT_ID('dbo.Lookup', 'U') IS NOT NULL DROP TABLE dbo.Lookup;
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NOT NULL DROP TABLE dbo.SchemaTemplateTenants;
IF OBJECT_ID('dbo.SchemaTemplateCreateSchemaTenants', 'U') IS NOT NULL DROP TABLE dbo.SchemaTemplateCreateSchemaTenants;
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');
IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName IN ('{ProductName}', 'SchemaTemplateCreateSchemaProduct');";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private static void DropTenantSchemasInternal(IDbCommand cmd, IEnumerable<string> tenants)
    {
        foreach (var tenant in tenants)
        {
            cmd.CommandText = $@"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + 'ALTER TABLE [' + s.name + '].[' + t.name + '] DROP CONSTRAINT [' + fk.name + '];' + CHAR(10)
  FROM sys.foreign_keys fk
  INNER JOIN sys.tables t ON fk.parent_object_id = t.object_id
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
SELECT @sql = @sql + 'DROP VIEW [' + s.name + '].[' + v.name + '];' + CHAR(10)
  FROM sys.views v
  INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
SELECT @sql = @sql + 'DROP PROCEDURE [' + s.name + '].[' + o.name + '];' + CHAR(10)
  FROM sys.objects o
  INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
  WHERE s.name = '{tenant}' AND o.type = 'P';
SELECT @sql = @sql + 'DROP TABLE [' + s.name + '].[' + t.name + '];' + CHAR(10)
  FROM sys.tables t
  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
  WHERE s.name = '{tenant}';
IF @sql <> '' EXEC sp_executesql @sql;
IF SCHEMA_ID('{tenant}') IS NOT NULL EXEC('DROP SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }
    }

    private void ExecuteOnMainDb(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private bool SchemaExists(string schemaName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.schemas WHERE name = @name";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = schemaName;
        cmd.Parameters.Add(p);
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result) > 0;
    }
}
