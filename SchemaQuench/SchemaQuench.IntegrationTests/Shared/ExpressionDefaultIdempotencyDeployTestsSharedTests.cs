// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System.IO;
using System;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using log4net;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// An expression column default — <c>uuid()</c> — must deploy once and then compare EQUAL, and a real
/// change to it must still be detected.
/// <para>Found by deploying <c>Demos/MariaDB/AdventureWorks</c>, which re-altered 29
/// <c>rowguid CHAR(36) … DEFAULT (uuid())</c> columns on every deploy, measured over four consecutive runs.
/// MySQL was clean, and that asymmetry named the cause: the comparison normalised only the LIVE value.
/// MariaDB's <c>SchemaSmith_NormalizeColumnDefault</c> override folds a function default by upper-casing it
/// and dropping an empty argument list, so live <c>uuid()</c> became <c>UUID</c> while the declared
/// <c>uuid()</c> was compared raw. <c>CURRENT_TIMESTAMP</c> survived only because packages already declare
/// it in the folded form, which is why this was never caught by the temporal defaults.</para>
/// <para>THE DRIFT PHASE IS THE HALF THAT MATTERS MOST. The obvious way to stop churn is to fold harder
/// until the two sides always agree, which silently stops managing the property altogether — a worse bug
/// than the churn, and invisible to an idempotency-only test. So this also drifts the default by hand and
/// requires the next deploy to put it back.</para>
/// </summary>
public abstract class ExpressionDefaultIdempotencyDeployTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void AnExpressionDefault_DeploysOnce_ThenComparesEqual_AndRealDriftIsStillCorrected()
    {
        var db = "TestExprDef_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"ExprDefault_{Guid.NewGuid():N}");
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            WritePackage(tempDir, db);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(serverConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();

            try
            {
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}`;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                ForgeKindler.KindleTheForge(cmd, Platform);

                // Below MySQL 8.0.13 there is no expression default to compare -- the column degrades, which
                // DefaultExpressionGatingTests covers.
                cmd.CommandText = "SELECT SchemaSmith_SupportsDefaultExpression()";
                if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    Assert.Ignore("Target does not support expression defaults (MySQL < 8.0.13).");

                config["SchemaPackagePath"] = tempDir;

                // Deploy 1 creates the table. Each deploy writes its summary to its own directory so one
                // run's report can never be read for another's.
                _environment.ClearReceivedCalls();
                RunSchemaQuench(LogDirFor(tempDir, 1));
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                Assert.That(LiveDefault(cmd, db), Does.Contain("uuid").IgnoreCase,
                    "deploy 1 must create the column with its declared expression default");

                // Deploy 2 must see no difference. Asserted on the change audit, which is what the
                // deployment summary's objectChanges is built from -- the thing that reported 29 modified
                // columns per run on the demo package.
                _environment.ClearReceivedCalls();
                RunSchemaQuench(LogDirFor(tempDir, 2));
                _environment.DidNotReceive().Exit(2);
                Assert.That(ModifiedColumnCount(LogDirFor(tempDir, 2)), Is.EqualTo(0),
                    "a re-deploy must not modify a column whose expression default is unchanged; the "
                    + "declared side was compared raw while the live side was folded, so they never matched");

                // Deploy 3: a REAL change must still be detected. Drift the default by hand to something
                // the package does not declare, then require the deploy to restore it.
                cmd.CommandText = $"ALTER TABLE `{db}`.`ExprDefaultProbe` "
                                  + "MODIFY COLUMN `RowGuid` CHAR(36) NOT NULL DEFAULT 'drifted';";
                cmd.ExecuteNonQuery();
                Assert.That(LiveDefault(cmd, db), Is.EqualTo("drifted"), "the drift must be in place first");

                _environment.ClearReceivedCalls();
                RunSchemaQuench(LogDirFor(tempDir, 3));
                _environment.DidNotReceive().Exit(2);
                Assert.Multiple(() =>
                {
                    Assert.That(LiveDefault(cmd, db), Does.Contain("uuid").IgnoreCase,
                        "a default that genuinely differs must still be corrected -- folding both sides "
                        + "must not turn the comparison into a no-op");
                    Assert.That(ModifiedColumnCount(LogDirFor(tempDir, 3)), Is.GreaterThan(0),
                        "and the correction must be reported, not applied silently");
                });
            }
            finally
            {
                config["SchemaPackagePath"] = string.Empty;
                try
                {
                    conn.ChangeDatabase("information_schema");
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`;";
                    cmd.ExecuteNonQuery();
                }
                // Typed rather than bare: a teardown drop can legitimately fail if the engine refuses it
                // (DbException) or the connection is no longer usable (InvalidOperationException). Anything
                // else here is a real fault and should not be swallowed by a passing test's cleanup.
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                // Release the appenders BEFORE deleting the tree: --LogPath points inside tempDir here, so
                // log4net still holds 'SchemaQuench - Errors.log' open and the recursive delete throws
                // IOException after every assertion has already passed -- a green test reported as failed.
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

    private static string LogDirFor(string tempDir, int deploy)
    {
        var dir = Path.Join(tempDir, $"logs{deploy}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LiveDefault(System.Data.IDbCommand cmd, string db)
    {
        cmd.CommandText = $"SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='{db}' "
                          + "AND TABLE_NAME='ExprDefaultProbe' AND COLUMN_NAME='RowGuid'";
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : value.ToString().Trim('\'');
    }

    /// <summary>
    /// The deployment summary's modified-column count, which is what a user actually reads.
    /// <para>Deliberately NOT read from <c>SchemaSmith_ChangeAudit</c>: that table is DRAINED when the
    /// summary is produced, so querying it after a run always returns zero and an assertion against it
    /// passes for the wrong reason in one direction and fails for the wrong reason in the other. Measured
    /// while writing this test — the audit came back empty on a deploy whose summary reported
    /// <c>modified: {columns: 1}</c>.</para>
    /// </summary>
    private static int ModifiedColumnCount(string logDir)
    {
        var summary = Path.Join(logDir, "SchemaQuench - Summary.json");
        Assert.That(File.Exists(summary), Is.True, $"no deployment summary was written to '{logDir}'");
        var changes = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(summary))["objectChanges"];
        return (int?)changes?["modified"]?["columns"] ?? 0;
    }

    private void WritePackage(string dir, string db)
    {
        var platform = Platform == Platform.MariaDb ? "MariaDb" : "MySQL";
        Directory.CreateDirectory(Path.Join(dir, "Templates", "Main", "Tables"));

        File.WriteAllText(Path.Join(dir, "Product.json"), $$"""
            {
              "Name": "ExprDefaultProbe",
              "ValidationScript": "SELECT 1",
              "TemplateOrder": ["Main"],
              "ScriptTokens": {},
              "ScriptFolders": [],
              "Platform": "{{platform}}"
            }
            """);

        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"), $$"""
            {
              "Name": "Main",
              "DatabaseIdentificationScript": "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{{db}}'",
              "ScriptFolders": []
            }
            """);

        // The exact shape AdventureWorks carries 29 of.
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Tables", "ExprDefaultProbe.json"), """
            {
              "Name": "`ExprDefaultProbe`",
              "Engine": "InnoDB",
              "Columns": [
                { "Name": "`Id`", "DataType": "int", "Nullable": false },
                { "Name": "`RowGuid`", "DataType": "char(36)", "Nullable": false, "Default": "uuid()" }
              ]
            }
            """);
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    /// <summary>
    /// The --LogPath switch has to be fed through the IEnvironment isolator, not through Main's args:
    /// CommandLineParser reads EnvironmentWrapper.GetFromFactory().CommandLine, so a substituted
    /// IEnvironment (which this fixture registers) makes every `--switch:value` invisible while a bare
    /// token like SkipKindlingForge still works because Program.Main inspects args[0] directly. Without
    /// this the summary lands in the test binary's own directory and the assertions read a stale one.
    /// </summary>
    private void RunSchemaQuench(string logDir)
    {
        _environment.CommandLine.Returns($"SchemaQuench.exe \"--LogPath:{logDir}\"");
        Program.Main(["SkipKindlingForge"]);
    }
}
