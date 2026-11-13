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
using System.Data;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Slice-5 selective execution integration tests (design §9.1–§9.5). Verifies the
/// <c>Target.Templates</c> / <c>Target.Databases</c> / <c>Target.Schemas</c> filter
/// surface against a live SQL Server: a fully-deployed multi-tenant product re-quenched
/// with a single-schema filter must only advance that schema's tracking, and the empty-
/// result / unknown-value diagnostics surface to the progress log.
/// </summary>
[Category("SqlServer")]
public class SelectiveExecutionTests
{
    private const string ProductName = "SchemaTemplateProduct";
    private const string TenantBodyTemplate = "TenantBody";

    private static readonly string[] DefaultTenants = ["tenant_acme", "tenant_beta", "tenant_globex"];

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;
    private readonly string _server;

    public SelectiveExecutionTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
        _server = config["Target:Server"];
    }

    [Test]
    public void Target_Schemas_Single_Tenant_Only_Advances_That_Tenants_Tracking()
    {
        // Quench 1: full multi-tenant deploy — every tenant has SeedTenantMarker tracked.
        // Drop in a new migration file that ALL tenants would normally pick up; quench 2 with
        // Target.Schemas = [tenant_acme] must produce a tracking row ONLY for tenant_acme.
        var product = TestHelper.CopyTestProduct("SqlServer", ProductName);
        var migrationFile = Path.Join(product, "Templates", "TenantBody", "MigrationScripts", "Before", "Migration_NewTenantTouch.sql");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = product;
            ClearTargetFilters(config);

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Write a new migration script that touches the per-tenant Customers table. Each
                // tenant should normally pick it up — but Target.Schemas = [tenant_acme] limits
                // it to one tenant for the second quench.
                File.WriteAllText(migrationFile, @"-- Per-tenant new-migration marker.
IF NOT EXISTS (SELECT 1 FROM [{{SchemaName}}].[Customers] WHERE CustomerID = 999)
    INSERT [{{SchemaName}}].[Customers] (CustomerID, Marker) VALUES (999, '{{SchemaName}}_new_migration');
");

                // Second quench: scope to tenant_acme only.
                config["Target:Schemas:0"] = "tenant_acme";
                _progressLog.ClearReceivedCalls();
                RunSchemaQuench();

                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Only tenant_acme should have the new tracking row.
                AssertMigrationTracked(TenantBodyTemplate, "tenant_acme",
                    "MigrationScripts/Before/Migration_NewTenantTouch.sql");
                AssertMigrationNotTracked(TenantBodyTemplate, "tenant_beta",
                    "MigrationScripts/Before/Migration_NewTenantTouch.sql");
                AssertMigrationNotTracked(TenantBodyTemplate, "tenant_globex",
                    "MigrationScripts/Before/Migration_NewTenantTouch.sql");

                // The per-tenant INSERT must have landed in tenant_acme but NOT the other two.
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM [tenant_acme].[Customers] WITH (NOLOCK) WHERE CustomerID = 999"),
                    Is.EqualTo(1), "tenant_acme must have the new-migration row.");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM [tenant_beta].[Customers] WITH (NOLOCK) WHERE CustomerID = 999"),
                    Is.EqualTo(0), "tenant_beta must NOT have been touched by the targeted second quench.");
                Assert.That(ScalarCount(
                    "SELECT COUNT(*) FROM [tenant_globex].[Customers] WITH (NOLOCK) WHERE CustomerID = 999"),
                    Is.EqualTo(0), "tenant_globex must NOT have been touched by the targeted second quench.");

                // The "Successfully Quenched" log line must appear only for tenant_acme on the
                // second quench — the other tenants' work units must have been filtered out.
                _progressLog.Received(1).Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_acme] Successfully Quenched");
                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_beta] Successfully Quenched");
                _progressLog.DidNotReceive().Info(
                    $"[{_server}].[{_mainDb}] [Schema: tenant_globex] Successfully Quenched");
            }
            finally
            {
                ClearTargetFilters(config);
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                Directory.Delete(product, true);
            }
        }
    }

    [Test]
    public void Target_Templates_Single_Template_Skips_Other_Templates_Discovery()
    {
        // Target.Templates = [Shared] must skip the TenantBody template entirely. The Shared
        // template's per-DB work unit still runs, but no [Schema: ...] iteration log appears.
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            config["Target:Templates:0"] = "Shared";

            try
            {
                RunSchemaQuench();
                _progressLog.DidNotReceive().Error(Arg.Any<string>());

                // Shared completed; no tenant iteration ran.
                _progressLog.Received(1).Info($"[{_server}].[{_mainDb}] Successfully Quenched");
                foreach (var tenant in DefaultTenants)
                    _progressLog.DidNotReceive().Info(
                        $"[{_server}].[{_mainDb}] [Schema: {tenant}] Successfully Quenched");

                // The startup target-resolution log must have fired and reported the filter and
                // post-filter unit count.
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("[Target] Templates:")));
                _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("[Target] Resolved")));
            }
            finally
            {
                ClearTargetFilters(config);
                DropTenantSchemas(DefaultTenants);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void Unknown_Filter_Value_Surfaces_Error_With_Available_Options()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            ResetTrackingAndCreateTenantSchemas(DefaultTenants);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", ProductName);
            ClearTargetFilters(config);
            config["Target:Schemas:0"] = "tenant_does_not_exist";

            try
            {
                RunSchemaQuench();

                // The error must mention the bad value AND list the discovered set so the user
                // can fix the typo. Both progress + error log must carry the message — a future
                // regression that sends to only one would silently break ops dashboards that
                // tail one stream.
                _progressLog.Received().Error(Arg.Is<string>(s =>
                    s.Contains("tenant_does_not_exist") &&
                    s.Contains("tenant_acme")));
                _errorLog.Received().Error(Arg.Is<string>(s =>
                    s.Contains("tenant_does_not_exist") &&
                    s.Contains("tenant_acme")));
            }
            finally
            {
                ClearTargetFilters(config);
                DropTenantSchemas(DefaultTenants);
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

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    /// <summary>
    /// Clears any Target:* filter array slots left over from a previous test in the same NUnit
    /// run. .NET's in-memory configuration provider is shared across tests in the same fixture,
    /// so a stale array index must be explicitly unset (setting to null + empty string both work;
    /// null removes the key, which is what `GetSection().GetChildren()` reads as "empty array").
    /// Enumerates the live config so any number of slots a prior test populated gets cleared,
    /// rather than hard-coding a 0..7 range that drifts as tests grow. `.ToList()` snapshots the
    /// children first — enumerating the live collection while nulling keys mutates it underfoot.
    /// </summary>
    private static void ClearTargetFilters(IConfigurationRoot config)
    {
        foreach (var dim in new[] { "Templates", "Databases", "Schemas" })
            foreach (var child in config.GetSection($"Target:{dim}").GetChildren().ToList())
                config[$"Target:{dim}:{child.Key}"] = null;
    }

    private static void ClearCheckpointsForProduct()
    {
        Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);
    }

    private void ResetTrackingAndCreateTenantSchemas(IEnumerable<string> tenants)
    {
        ClearCheckpointsForProduct();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = @$"
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}';
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NOT NULL DELETE FROM dbo.SchemaTemplateTenants;
IF OBJECT_ID('dbo.Lookup', 'U') IS NOT NULL DELETE FROM dbo.Lookup;";
        cmd.ExecuteNonQuery();

        DropTenantSchemasInternal(cmd, tenants);

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"IF SCHEMA_ID('{tenant}') IS NULL EXEC('CREATE SCHEMA [{tenant}]');";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = @"
IF OBJECT_ID('dbo.SchemaTemplateTenants', 'U') IS NULL
    CREATE TABLE dbo.SchemaTemplateTenants ([Name] NVARCHAR(128) NOT NULL CONSTRAINT PK_SchemaTemplateTenants PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        foreach (var tenant in tenants)
        {
            cmd.CommandText = $"INSERT INTO dbo.SchemaTemplateTenants ([Name]) VALUES (N'{tenant}');";
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
IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts', 'U') IS NOT NULL
    DELETE FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = '{ProductName}';";
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

    private void AssertMigrationTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND ScriptPath = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(1),
            $"Expected a tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}').");
    }

    private void AssertMigrationNotTracked(string templateName, string schemaName, string scriptPath)
    {
        var count = ScalarCount(
            $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE ProductName = '{ProductName}' AND template_name = '{templateName}' AND schema_name = '{schemaName}' AND ScriptPath = '{scriptPath}'");
        Assert.That(count, Is.EqualTo(0),
            $"Expected NO tracking row for (template='{templateName}', schema='{schemaName}', script='{scriptPath}') — the targeted run must not have touched this scope.");
    }

    private int ScalarCount(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result);
    }
}
