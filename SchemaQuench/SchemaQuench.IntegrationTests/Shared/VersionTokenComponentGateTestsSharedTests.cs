// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using log4net;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A table or column gated on an engine token (<c>{{ServerMajorVersion}}</c>, <c>{{CompatibilityLevel}}</c>) deploys only
/// where its expression holds, as on the other engines; this is how a column variant declares a type only a newer
/// release has.
/// </summary>
public abstract class VersionTokenComponentGateTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void TablesAndColumnsGatedOnTheEngineTokens_DeployOnlyWhereTheirExpressionHolds()
    {
        var db = "TestVerGate_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"VersionGate_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            WritePackage(tempDir, db);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(BaseConnectionString + "Database=information_schema;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();

            try
            {
                cmd.CommandText = $"CREATE DATABASE `{db}`;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                ForgeKindler.KindleTheForge(cmd, Platform);
                config["SchemaPackagePath"] = tempDir;

                _environment.CommandLine.Returns($"SchemaQuench.exe \"--LogPath:{Path.Join(tempDir, "logs")}\"");
                Program.Main(["SkipKindlingForge"]);

                var errors = string.Join(Environment.NewLine, _errorLog.ReceivedCalls().Concat(_progressLog.ReceivedCalls())
                    .Where(c => c.GetMethodInfo().Name is "Error" or "Warn" or "Fatal")
                    .Select(c => c.GetArguments().FirstOrDefault()?.ToString()));
                Assert.That(_environment.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Exit").Select(c => c.GetArguments()[0]),
                    Has.None.EqualTo(2).And.None.EqualTo(3), "the deploy failed:" + Environment.NewLine + errors);
                Assert.Multiple(() =>
                {
                    Assert.That(Names(cmd, $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME LIKE 'Gated%'"),
                        Is.EqualTo(new[] { "GatedOn" }).IgnoreCase, "a table gated on {{CompatibilityLevel}}");
                    Assert.That(Names(cmd, $"SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'GatedOn'"),
                        Is.EqualTo(new[] { "Id", "Modern" }), "columns gated on {{ServerMajorVersion}}");
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
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                // --LogPath points inside tempDir, so log4net must let go of its files before the delete.
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

    private static List<string> Names(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var names = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                names.Add(reader.GetString(0));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private void WritePackage(string dir, string db)
    {
        var platform = Platform == Platform.MariaDb ? "MariaDb" : "MySQL";
        Directory.CreateDirectory(Path.Join(dir, "Templates", "Main", "Tables"));
        File.WriteAllText(Path.Join(dir, "Product.json"), $$"""
            { "Name": "VersionGateProbe", "ValidationScript": "SELECT 1", "TemplateOrder": ["Main"], "Platform": "{{platform}}" }
            """);
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{{db}}'" }
            """);
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Tables", "GatedOn.json"), """
            { "Name": "`GatedOn`", "ShouldApplyExpression": "{{CompatibilityLevel}} > 0",
              "Columns": [
                { "Name": "`Id`", "DataType": "int", "Nullable": false },
                { "Name": "`Modern`", "DataType": "int", "Nullable": true, "ShouldApplyExpression": "{{ServerMajorVersion}} > 0" },
                { "Name": "`Never`", "DataType": "int", "Nullable": true, "ShouldApplyExpression": "{{ServerMajorVersion}} < 0" }
              ] }
            """);
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Tables", "GatedOff.json"), """
            { "Name": "`GatedOff`", "ShouldApplyExpression": "{{CompatibilityLevel}} < 0",
              "Columns": [ { "Name": "`Id`", "DataType": "int", "Nullable": false } ] }
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
}
