// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Configuration;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A regular template whose DatabaseIdentificationScript returns several databases, deployed with <c>MaxThreads</c> 4: the
/// databases are deployed at once, and each must get every script with its own query-token values (#475). The SQL
/// Server half, at one thread and four, is <c>RegularTemplateFanOutTests</c>.
/// </summary>
[NonParallelizable]
public abstract class ParallelFanOutSharedTests
{
    /// <summary>The Product.json Platform value.</summary>
    protected abstract string ProductPlatform { get; }
    /// <summary>A connection to the server, not to any one of the test's databases.</summary>
    protected abstract IDbConnection OpenServer();
    protected abstract string CreateDatabaseSql(string db);
    protected abstract void DropDatabase(IDbCommand cmd, string db);
    protected abstract string IdentificationScript(string prefix);
    protected abstract string CurrentDatabaseQuery { get; }
    protected abstract string ProbeFileName { get; }
    /// <summary>An Objects-slot script that leaves an object returning <c>{{Who}}</c>.</summary>
    protected abstract string ProbeScript { get; }
    /// <summary>Returns what the probe returns, or NULL when it is missing.</summary>
    protected abstract string ProbeValueSql { get; }
    protected abstract string AfterScript { get; }
    protected abstract string AfterMarkerExistsSql { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _prefix = "fanout_" + Guid.NewGuid().ToString("N")[..8];
    private string[] Databases => Enumerable.Range(1, 3).Select(i => $"{_prefix}_db{i}").ToArray();

    [OneTimeTearDown]
    public void DropDatabases()
    {
        using var conn = OpenServer();
        using var cmd = conn.CreateCommand();
        foreach (var db in Databases)
        {
            try { DropDatabase(cmd, db); }
            catch (System.Data.Common.DbException) { /* best-effort cleanup */ }
        }
    }

    [Test]
    public void EveryDatabaseGetsEveryScript_WithItsOwnQueryTokenValues_WhenDeployedInParallel()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"{_prefix}_pkg");
        lock (FactoryContainer.SharedLockObject)
        {
            var errors = new System.Collections.Generic.List<string>();
            _progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString()));
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var savedPath = config["SchemaPackagePath"];
            var savedThreads = config[SettingsKeys.MaxThreads];
            try
            {
                using (var conn = OpenServer())
                using (var cmd = conn.CreateCommand())
                {
                    foreach (var db in Databases)
                    {
                        cmd.CommandText = CreateDatabaseSql(db);
                        cmd.ExecuteNonQuery();
                    }
                }
                WritePackage(tempDir);
                config["SchemaPackagePath"] = tempDir;
                config[SettingsKeys.MaxThreads] = "4";

                Program.Main([]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));

                using var check = OpenServer();
                using var query = check.CreateCommand();
                Assert.Multiple(() =>
                {
                    Assert.That(errors, Is.Empty, "the run logged errors");
                    foreach (var db in Databases)
                    {
                        check.ChangeDatabase(db);
                        query.CommandText = ProbeValueSql;
                        Assert.That(query.ExecuteScalar()?.ToString(), Is.EqualTo(db), $"{db} must get the Objects-slot script, with {db}'s own query-token value");
                        query.CommandText = AfterMarkerExistsSql;
                        Assert.That(Convert.ToInt64(query.ExecuteScalar()), Is.EqualTo(1), $"{db} must get the After-slot script");
                    }
                });
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                config[SettingsKeys.MaxThreads] = savedThreads;
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

    private void WritePackage(string dir)
    {
        var template = Path.Join(dir, "Templates", "Fanout");
        Directory.CreateDirectory(Path.Join(template, "Procedures"));
        Directory.CreateDirectory(Path.Join(template, "After"));
        File.WriteAllText(Path.Join(dir, "Product.json"),
            $"{{ \"Name\": \"FanoutProbe\", \"ValidationScript\": \"SELECT 1\", \"TemplateOrder\": [\"Fanout\"], \"ScriptTokens\": {{}}, \"ScriptFolders\": [], \"Platform\": \"{ProductPlatform}\" }}");
        File.WriteAllText(Path.Join(template, "Template.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            Name = "Fanout",
            DatabaseIdentificationScript = IdentificationScript(_prefix),
            ScriptTokens = new { Who = "<*Query*>" + CurrentDatabaseQuery },
            ScriptFolders = new object[]
            {
                new { FolderPath = "Procedures", QuenchSlot = "Objects", ObjectType = "Procedures" },
                new { FolderPath = "After", QuenchSlot = "After" }
            }
        }));
        File.WriteAllText(Path.Join(template, "Procedures", ProbeFileName), ProbeScript);
        File.WriteAllText(Path.Join(template, "After", "AfterMarker.sql"), AfterScript);
    }
}
