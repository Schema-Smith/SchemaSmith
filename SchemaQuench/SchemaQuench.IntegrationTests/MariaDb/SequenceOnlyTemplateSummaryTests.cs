// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data.Common;
using System.IO;
using log4net;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// A template that declares sequences and no tables still reports what its sequences did: the deployment summary
/// reads them back, and below MariaDB 10.3, where a sequence is skipped, the downgrade sets the outcome and exit code.
/// Run through Program.Main, because the run decides here whether a template's changes are read back at all.
/// </summary>
[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class SequenceOnlyTemplateSummaryTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void ASequenceOnlyTemplate_IsReportedInTheSummary()
    {
        var db = "TestSeqOnly_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"SeqOnly_{Guid.NewGuid():N}");
        var logDir = Path.Join(tempDir, "logs");

        lock (FactoryContainer.SharedLockObject)
        {
            _progressLog.ClearReceivedCalls();
            _errorLog.ClearReceivedCalls();
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(FixtureSetup.Config);
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            WritePackage(tempDir, db);
            Directory.CreateDirectory(logDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.ConnectionString + "Database=information_schema;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();

            try
            {
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}`;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                ForgeKindler.KindleTheForge(cmd, Platform.MariaDb);
                cmd.CommandText = "SELECT SchemaSmith_ServerVersionNum()";
                var hasSequences = Convert.ToInt32(cmd.ExecuteScalar()) >= 1003;
                config["SchemaPackagePath"] = tempDir;

                _environment.CommandLine.Returns($"SchemaQuench.exe \"--LogPath:{logDir}\"");
                Program.Main(["SkipKindlingForge"]);

                var summary = JObject.Parse(File.ReadAllText(Path.Join(logDir, "SchemaQuench - Summary.json")));
                Assert.Multiple(() =>
                {
                    Assert.That((bool?)summary["objectChanges"]?["instrumented"], Is.True, "the template's changes are read back");
                    if (hasSequences)
                    {
                        Assert.That((int?)summary["run"]?["exitCode"], Is.Zero);
                    }
                    else
                    {
                        Assert.That((string)summary["run"]?["outcome"], Is.EqualTo("Incomplete"));
                        Assert.That((int?)summary["run"]?["exitCode"], Is.EqualTo(1));
                        Assert.That(summary["unsupportedDowngrade"]?.ToString(), Does.Contain("small_no"));
                    }
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

    private static void WritePackage(string dir, string db)
    {
        Directory.CreateDirectory(Path.Join(dir, "Templates", "Main", "Sequences"));
        File.WriteAllText(Path.Join(dir, "Product.json"), """
            { "Name": "SeqOnlyProbe", "ValidationScript": "SELECT 1", "TemplateOrder": ["Main"], "ScriptTokens": {}, "ScriptFolders": [], "Platform": "MariaDb" }
            """);
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{{db}}'", "ScriptFolders": [] }
            """);
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Sequences", "small_no.json"), """{ "Name": "small_no" }""");
    }
}
