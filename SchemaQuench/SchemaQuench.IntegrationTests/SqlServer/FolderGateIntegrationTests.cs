// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Folder-gate (#260) engine-facing contract against a live SQL Server: a folder's
/// <c>ShouldApplyExpression</c> is run as a scalar query and interpreted as a boolean. The
/// folder-filtering / slot-rebuild logic is engine-agnostic C# (unit-covered); what's genuinely
/// engine-specific is how each engine returns a boolean scalar, so that's what these per-engine
/// tests pin (SQL Server returns bit/int here).
/// </summary>
[Category("SqlServer")]
public class FolderGateIntegrationTests
{
    private readonly string _connectionString;
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    public FolderGateIntegrationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    [Test]
    public void FolderGate_LiveSqlServer_InterpretsBooleanPredicates()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.Multiple(() =>
        {
            Assert.That(FolderGate.ShouldApply(cmd, ""), Is.True, "Blank expression always applies.");
            Assert.That(FolderGate.ShouldApply(cmd, "SELECT 1"), Is.True);
            Assert.That(FolderGate.ShouldApply(cmd, "SELECT 0"), Is.False);
            Assert.That(
                FolderGate.ShouldApply(cmd, "SELECT CASE WHEN @@VERSION LIKE '%Microsoft%' THEN 1 ELSE 0 END"),
                Is.True, "A real server-property predicate evaluates true on SQL Server.");
        });
    }

    [Test]
    public void FolderGate_LiveSqlServer_EvaluatesResolvedScriptToken()
    {
        // #260 fix: a gate may reference a script token, which is resolved before evaluation.
        // Pre-fix the unresolved '{{EnvType}}' would never equal 'prod' and the gate would read false.
        var folder = new TemplateFolder
        {
            FolderPath = "EnvGated",
            QuenchSlot = TemplateQuenchSlot.Before,
            ShouldApplyExpression = "SELECT CASE WHEN '{{EnvType}}' = 'prod' THEN 1 ELSE 0 END"
        };
        folder.LoadSqlFiles(NonexistentBasePath(), [new KeyValuePair<string, string>("EnvType", "prod")], Platform.SqlServer);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.That(FolderGate.ShouldApply(cmd, folder.ShouldApplyExpression), Is.True);
    }

    private static string NonexistentBasePath() =>
        Path.Combine(Path.GetTempPath(), "ss-folder-gate-" + Guid.NewGuid().ToString("N"));

    [Test]
    public void GateProductFolders_LiveSqlServer_ResolvesServerMajorVersionButNotCompatibilityLevel()
    {
        // B6b: product-folder gates (ProductQuench.GateProductFolders) run at product scope, before any
        // database is selected — the server connection is already open, so {{ServerMajorVersion}} must
        // resolve there. {{CompatibilityLevel}} is a property of a database and none is selected yet, so
        // it stays literal — a real server rejects the unresolved token rather than the gate silently
        // being rewritten into a wrong-but-plausible comparison.
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("SqlServer", "ValidProduct");
            try
            {
                var quench = new ProductQuench();

                using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();

                var versioned = new ProductFolder { FolderPath = "versioned", ShouldApplyExpression = "{{ServerMajorVersion}} >= 1" };
                var survivors = quench.GateProductFolders(cmd, new[] { versioned });
                Assert.That(survivors.Select(f => f.FolderPath), Is.EqualTo(new[] { "versioned" }),
                    "A real SQL Server always reports a major version >= 1; an unresolved literal token would fail instead.");

                // Assert.Catch (not Assert.Throws<Exception>) accepts the derived exception type each
                // provider actually throws (e.g. SqlException) rather than requiring the exact base type.
                // Two things distinguish "correctly left literal" from "failed for some other reason":
                // the command text still carries the unresolved token, and the server's own rejection is
                // a syntax error on the brace — not a connection failure or something unrelated.
                var compatGated = new ProductFolder { FolderPath = "compat-gated", ShouldApplyExpression = "{{CompatibilityLevel}} >= 100" };
                var ex = Assert.Catch(() => quench.GateProductFolders(cmd, new[] { compatGated }));
                Assert.That(cmd.CommandText, Does.Contain("{{CompatibilityLevel}}"),
                    "{{CompatibilityLevel}} is not resolvable at product scope, so the evaluated SQL must still carry it literally.");
                Assert.That(ex.Message, Does.Contain("{"),
                    "The server must reject the literal brace as a syntax error, not fail for an unrelated reason.");
            }
            finally
            {
                config["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void FolderGate_SamePackageTwoCompatLevels_DeploysTheMatchingVariantToEach()
    {
        var highDb = $"FGCompatHigh_{Guid.NewGuid():N}".Substring(0, 24);
        var lowDb = $"FGCompatLow_{Guid.NewGuid():N}".Substring(0, 24);

        lock (FactoryContainer.SharedLockObject)
        {
            var highLevel = CreateDatabaseAtMaxCompatLevel(highDb);
            CreateDatabaseAtCompatLevel(lowDb, 130);
            try
            {
                var config = FactoryContainer.Resolve<IConfigurationRoot>();
                config["SchemaPackagePath"] =
                    TestHelper.GetTestProductPath("SqlServer", "FolderGateCompatSplitProduct");
                config["ScriptTokens:HighCompatDB"] = highDb;
                config["ScriptTokens:LowCompatDB"] = lowDb;
                config["ScriptTokens:CompatBoundary"] = highLevel.ToString();

                // Program.Main ends by calling Environment.Exit via LogBackup; a substituted
                // IEnvironment stops that from tearing down the test host.
                FactoryContainer.Register(_environment);
                Program.Main(System.Array.Empty<string>());

                Assert.Multiple(() =>
                {
                    Assert.That(ReadProbeVariant(highDb), Is.EqualTo("Modern"),
                        "The high-compat database must get the Modern folder.");
                    Assert.That(ReadProbeVariant(lowDb), Is.EqualTo("Legacy"),
                        "The low-compat database must get the Legacy folder.");
                });
            }
            finally
            {
                FactoryContainer.Unregister<IEnvironment>();
                DropDatabase(highDb);
                DropDatabase(lowDb);
            }
        }
    }

    private int CreateDatabaseAtMaxCompatLevel(string dbName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{dbName}];";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) * 10;";
        var maxLevel = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = $"ALTER DATABASE [{dbName}] SET COMPATIBILITY_LEVEL = {maxLevel};";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(dbName);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
        return maxLevel;
    }

    private void CreateDatabaseAtCompatLevel(string dbName, int level)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{dbName}]; ALTER DATABASE [{dbName}] SET COMPATIBILITY_LEVEL = {level};";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(dbName);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
    }

    private string ReadProbeVariant(string dbName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Variant FROM dbo.vCompatProbe;";
        return (string)cmd.ExecuteScalar();
    }

    private void DropDatabase(string dbName)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        cmd.CommandText = $@"
IF DB_ID('{dbName}') IS NOT NULL
  ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{dbName}];";
        cmd.ExecuteNonQuery();
    }
}
