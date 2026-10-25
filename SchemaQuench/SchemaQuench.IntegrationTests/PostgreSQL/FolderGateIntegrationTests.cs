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

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Folder-gate (#260) engine-facing contract against a live PostgreSQL: PostgreSQL returns a native
/// <c>boolean</c> scalar, so this pins that <see cref="FolderGate.ShouldApply"/> interprets it
/// correctly. The folder-filtering / slot-rebuild logic itself is engine-agnostic (unit-covered).
/// </summary>
[Category("PostgreSQL")]
public class FolderGateIntegrationTests
{
    private readonly string _connectionString;

    public FolderGateIntegrationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    // A product script's RAISE NOTICE is logged as a template script's is: under VerboseLogging, and not otherwise.
    [TestCase("true", true)]
    [TestCase(null, false)]
    public void AProductScriptsNotices_AreLoggedUnderVerboseLogging(string verbose, bool logged)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var scriptDir = Path.Join(Path.GetTempPath(), $"ProductNotice_{Guid.NewGuid():N}");
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "ValidProduct");
            var wasVerbose = config["VerboseLogging"];
            config["VerboseLogging"] = verbose;
            var progress = new List<string>();
            var progressLog = Substitute.For<log4net.ILog>();
            progressLog.When(l => l.Info(Arg.Any<object>())).Do(ci => progress.Add($"{ci.Arg<object>()}"));
            LogFactory.Register("ProgressLog", progressLog);
            LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());
            try
            {
                Directory.CreateDirectory(scriptDir);
                var path = Path.Join(scriptDir, "notice.sql");
                File.WriteAllText(path, "DO $$ BEGIN RAISE NOTICE 'product script says hello'; END $$;");
                var folder = new ProductFolder { FolderPath = "Notices", QuenchSlot = ProductQuenchSlot.After };
                folder.Scripts.Add(SqlScript.Load(path, [], Platform.PostgreSQL));

                new ProductQuench().QuenchProductScriptsWithCheckpoint([folder], "After Product", false);

                Assert.That(progress.Any(l => l.EndsWith("product script says hello")), Is.EqualTo(logged), string.Join(Environment.NewLine, progress));
            }
            finally
            {
                config["SchemaPackagePath"] = null;
                config["VerboseLogging"] = wasVerbose;
                LogFactory.Clear();
                if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, true);
            }
        }
    }

    [Test]
    public void FolderGate_LivePostgreSql_InterpretsBooleanPredicates()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.Multiple(() =>
        {
            Assert.That(FolderGate.ShouldApply(cmd, ""), Is.True, "Blank expression always applies.");
            Assert.That(FolderGate.ShouldApply(cmd, "SELECT true"), Is.True);
            Assert.That(FolderGate.ShouldApply(cmd, "SELECT false"), Is.False);
            Assert.That(
                FolderGate.ShouldApply(cmd, "SELECT version() ILIKE '%PostgreSQL%'"),
                Is.True, "A real native-boolean predicate evaluates true on PostgreSQL.");
        });
    }

    [Test]
    public void FolderGate_LivePostgreSql_EvaluatesResolvedScriptToken()
    {
        // #260 fix: a gate may reference a script token, which is resolved before evaluation.
        // Pre-fix the unresolved '{{EnvType}}' would never equal 'prod' and the gate would read false.
        var folder = new TemplateFolder
        {
            FolderPath = "EnvGated",
            QuenchSlot = TemplateQuenchSlot.Before,
            ShouldApplyExpression = "SELECT '{{EnvType}}' = 'prod'"
        };
        folder.LoadSqlFiles(NonexistentBasePath(), [new KeyValuePair<string, string>("EnvType", "prod")], Platform.PostgreSQL);

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.That(FolderGate.ShouldApply(cmd, folder.ShouldApplyExpression), Is.True);
    }

    [Test]
    public void VersionTokens_CompatibilityLevelFallsBackToServerVersion()
    {
        // A1 parity: PostgreSQL has no compatibility-level concept, so detection returns a null
        // CompatibilityLevel and {{CompatibilityLevel}} falls back to the detected server version — a
        // gate comparing the two tokens is true. PostgreSQL returns a native boolean here.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var info = TargetVersionDetector.Detect(cmd, Platform.PostgreSQL);
        Assert.Multiple(() =>
        {
            Assert.That(info.CompatibilityLevel, Is.Null, "PostgreSQL has no compatibility-level concept.");
            Assert.That(info.ServerComparable, Is.GreaterThan(0));
        });

        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var folder = new TemplateFolder
        {
            FolderPath = "parity",
            QuenchSlot = TemplateQuenchSlot.Before,
            ShouldApplyExpression = "SELECT {{CompatibilityLevel}} = {{ServerMajorVersion}}"
        };
        folder.Scripts.Add(new SqlScript { Name = "parity.sql" });
        template.ScriptFolders.Add(folder);

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(info.ServerComparable, info.CompatibilityLevel);
        quench.ApplyFolderGates(cmd);

        Assert.That(quench.IterationBeforeScripts.Single().Name, Is.EqualTo("parity.sql"));
    }

    private static string NonexistentBasePath() =>
        Path.Combine(Path.GetTempPath(), "ss-folder-gate-" + Guid.NewGuid().ToString("N"));
}
