// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// An object script whose text and whose module the server still renders as last applied is not re-run. Every
/// engine: deploy a view, a function, a procedure and a trigger twice; the second deploy runs none of them; a hand
/// change on the server brings that one back; a text change in the package brings that one back; an [ALWAYS] script
/// and the setting turned off run everything. The subclass supplies the engine's DDL.
/// </summary>
[NonParallelizable]
public abstract class UnchangedModuleSkipSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string ConfigPrefix { get; }
    /// <summary>The table JSON the trigger needs, in the engine's table-file shape.</summary>
    protected abstract string TableJson { get; }
    /// <summary>Folder-relative script files: Views/, Functions/, Procedures/, Triggers/.</summary>
    protected abstract Dictionary<string, string> Scripts { get; }
    /// <summary>A statement that changes the deployed view on the server behind SchemaSmith's back.</summary>
    protected abstract string AlterViewByHand { get; }
    /// <summary>The function script with a changed body.</summary>
    protected abstract string ChangedFunctionScript { get; }
    protected abstract string[] DropStatements { get; }

    private IDbConnection _connection = null!;
    private string _packageDir = "";

    /// <summary>A connection string from the engine's own test settings, landing on the main database.</summary>
    protected string TestConnection(string initialDatabase)
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        return ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1", initialDatabase, config[$"{ConfigPrefix}:User"], config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"],
            ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"));
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        if (Platform.GetBasePlatform() != Platform.MySQL) _connection.ChangeDatabase(MainDb);
        _packageDir = Path.Join(Path.GetTempPath(), $"UnchangedSkip_{Guid.NewGuid():N}");
    }

    // Each test starts from no objects: a dropped module renders as nothing, so its stamp cannot match.
    [SetUp]
    public void SetUp()
    {
        foreach (var drop in DropStatements) Exec(drop);
        WritePackage(Scripts);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            foreach (var drop in DropStatements) Exec(drop);
        }
        finally
        {
            _connection?.Close();
            _connection?.Dispose();
            if (Directory.Exists(_packageDir)) Directory.Delete(_packageDir, true);
        }
    }

    [Test]
    public void SecondDeploy_RunsNoModule_HandChangeAndTextChangeBringTheirScriptBack()
    {
        var first = Deploy();
        Assert.That(first.Values, Is.All.EqualTo(ScriptOutcome.Applied), "first deploy: " + Describe(first));

        var second = Deploy();
        Assert.That(second.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "an unchanged package must not re-run any module: " + Describe(second));

        Exec(AlterViewByHand);
        var third = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(third["Views"], Is.EqualTo(ScriptOutcome.Applied), "a view changed on the server must be re-applied: " + Describe(third));
            Assert.That(third.Where(kv => kv.Key != "Views").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(third));
        });

        var changed = new Dictionary<string, string>(Scripts) { ["Functions"] = ChangedFunctionScript };
        WritePackage(changed);
        var fourth = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(fourth["Functions"], Is.EqualTo(ScriptOutcome.Applied), "a function whose text changed must be re-applied: " + Describe(fourth));
            Assert.That(fourth.Where(kv => kv.Key != "Functions").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(fourth));
        });

        var fifth = Deploy();
        Assert.That(fifth.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "the re-applied function must be stamped again: " + Describe(fifth));
    }

    [Test]
    public void AlwaysMarker_Sentinel_RunScriptsTwice_AndTheSettingOff_RunEveryTime()
    {
        WritePackage(Scripts, viewFileName: "v_unchanged [ALWAYS].sql");
        Deploy();
        var second = Deploy();
        Assert.That(second["Views"], Is.EqualTo(ScriptOutcome.Applied), "an [ALWAYS] script runs every time: " + Describe(second));

        var sentinel = new Dictionary<string, string>(Scripts) { ["Views"] = "-- SchemaSmith: AlwaysRun\n" + Scripts["Views"] };
        WritePackage(sentinel);
        Deploy();
        var third = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(third["Views"], Is.EqualTo(ScriptOutcome.Applied), "a script carrying the AlwaysRun sentinel runs every time: " + Describe(third));
            Assert.That(third.Where(kv => kv.Key != "Views").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(third));
        });

        WritePackage(Scripts);
        Deploy();
        var twice = Deploy(runTwice: true);
        Assert.That(twice.Values, Is.All.EqualTo(ScriptOutcome.Applied), "RunScriptsTwice runs every script: " + Describe(twice));
        var off = Deploy(skipUnchanged: false);
        Assert.That(off.Values, Is.All.EqualTo(ScriptOutcome.Applied), "with SkipUnchangedObjects off every script runs: " + Describe(off));
    }

    [Test]
    public void CustomFolderWithoutObjectType_RunsEveryTime()
    {
        WritePackage(Scripts, customFolder: true);
        Deploy();
        var second = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(second["Custom"], Is.EqualTo(ScriptOutcome.Applied), "a script from a folder declared without an ObjectType is not stamped: " + Describe(second));
            Assert.That(second.Where(kv => kv.Key != "Custom").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(second));
        });
    }

    [Test]
    public void WhatIf_ReportsUnchangedScripts_WouldReapplyAHandChange_AndStampsNothing()
    {
        WritePackage(Scripts);
        Deploy();
        var whatIf = Deploy(whatIf: true);
        Assert.That(whatIf.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "WhatIf after a deploy reports every module unchanged: " + Describe(whatIf));

        Exec(AlterViewByHand);
        var afterHandChange = Deploy(whatIf: true);
        Assert.Multiple(() =>
        {
            Assert.That(afterHandChange["Views"], Is.EqualTo(ScriptOutcome.Applied), "WhatIf would re-apply a view changed on the server: " + Describe(afterHandChange));
            Assert.That(afterHandChange.Where(kv => kv.Key != "Views").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(afterHandChange));
        });

        var real = Deploy();
        Assert.That(real["Views"], Is.EqualTo(ScriptOutcome.Applied), "WhatIf wrote no stamp, so the real deploy still sees the hand change: " + Describe(real));
    }

    private static string Describe(Dictionary<string, ScriptOutcome> outcomes) => string.Join(", ", outcomes.Select(kv => $"{kv.Key}={kv.Value}"));

    private void WritePackage(Dictionary<string, string> scripts, string viewFileName = "v_unchanged.sql", bool customFolder = false)
    {
        if (Directory.Exists(_packageDir)) Directory.Delete(_packageDir, true);
        var templateDir = Path.Join(_packageDir, "Templates", "Main");
        foreach (var folder in new[] { "Tables", "Views", "Functions", "Procedures", "Triggers", "Custom" })
            Directory.CreateDirectory(Path.Join(templateDir, folder));
        // A folder declared without an ObjectType: the design's opt-out for scripts with side effects.
        var customFolderJson = customFolder ? """, { "FolderPath": "Custom", "QuenchSlot": "Objects" }""" : "";
        File.WriteAllText(Path.Join(_packageDir, "Product.json"), $$"""
            { "Name": "UnchangedSkipProduct", "Platform": "{{Platform}}", "TemplateOrder": [ "Main" ] }
            """);
        File.WriteAllText(Path.Join(templateDir, "Template.json"), $$"""
            { "Name": "Main", "DatabaseIdentificationScript": "SELECT '{{MainDb}}'",
              "ScriptFolders": [ { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" }, { "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" },
                                 { "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }, { "FolderPath": "Triggers", "QuenchSlot": "AfterTablesObjects", "ObjectType": "Triggers" }{{customFolderJson}} ] }
            """);
        File.WriteAllText(Path.Join(templateDir, "Tables", "unchanged_t.json"), TableJson);
        File.WriteAllText(Path.Join(templateDir, "Views", viewFileName), scripts["Views"]);
        File.WriteAllText(Path.Join(templateDir, "Functions", "f_unchanged.sql"), scripts["Functions"]);
        File.WriteAllText(Path.Join(templateDir, "Procedures", "p_unchanged.sql"), scripts["Procedures"]);
        File.WriteAllText(Path.Join(templateDir, "Triggers", "tr_unchanged.sql"), scripts["Triggers"]);
        if (customFolder) File.WriteAllText(Path.Join(templateDir, "Custom", "p_custom.sql"), scripts["Procedures"]);
    }

    private Dictionary<string, ScriptOutcome> Deploy(bool skipUnchanged = true, bool whatIf = false, bool runTwice = false)
    {
        var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
        lock (FactoryContainer.SharedLockObject)
        {
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                var settings = new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = _packageDir,
                    ["Target:User"] = config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                    ["Target:Password"] = config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"],
                    ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"],
                    ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1",
                    ["Target:SkipUnchangedObjects"] = skipUnchanged ? "true" : "false"
                };
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    settings[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(settings!).Build());

                var product = Product.Load();
                var template = Template.Load("Main", product);
                // The deploy's own log, so a failed step says why in the assertion rather than in a file nobody reads.
                var log = new List<string>();
                var progressLog = NSubstitute.Substitute.For<log4net.ILog>();
                progressLog.When(l => l.Info(NSubstitute.Arg.Any<object>())).Do(ci => log.Add(ci.ArgAt<object>(0)?.ToString()));
                progressLog.When(l => l.Warn(NSubstitute.Arg.Any<object>())).Do(ci => log.Add("WARN " + ci.ArgAt<object>(0)));
                progressLog.When(l => l.Error(NSubstitute.Arg.Any<object>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0)));
                var errorLog = NSubstitute.Substitute.For<log4net.ILog>();
                errorLog.When(l => l.Error(NSubstitute.Arg.Any<object>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0)));
                errorLog.When(l => l.Error(NSubstitute.Arg.Any<object>(), NSubstitute.Arg.Any<Exception>())).Do(ci => log.Add("ERROR " + ci.ArgAt<object>(0) + " " + ci.ArgAt<Exception>(1)?.Message));
                LogFactory.Register("ProgressLog", progressLog);
                LogFactory.Register("ErrorLog", errorLog);
                // The PostgreSQL quench renders these as booleans, the others as 0/1.
                var no = Platform == Platform.PostgreSQL ? "false" : "0";
                var yes = Platform == Platform.PostgreSQL ? "true" : "1";
                var quench = new DatabaseQuench("127.0.0.1", product, template, MainDb,
                    suppressKindling: false, whatIfOnly: whatIf ? yes : no, runScriptsTwice: runTwice,
                    dropRemovedTables: no, dropRemovedColumns: yes, dropRemovedForeignKeys: yes, dropRemovedCheckConstraints: yes, dropRemovedExcludeConstraints: yes, dropRemovedStatistics: yes, dropRemovedIndexes: yes, dropUnknownIndexes: false, updateTables: true,
                    deliverData: false, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();
                var errors = quench.IterationObjectScripts.Concat(quench.IterationAfterTablesObjectScripts)
                    .Where(s => s.Error != null).Select(s => $"{Path.GetFileName(s.LogPath)}: {s.Error.Message}");
                Assert.That(quench.QuenchSuccessful, Is.True, "the deploy must succeed; script errors: " + string.Join(" | ", errors)
                    + "\nlog: " + string.Join("\n", log.Where(l => l != null && (l.StartsWith("ERROR") || l.StartsWith("WARN") || l.Contains("Quenching")))));

                var outcomes = new Dictionary<string, ScriptOutcome>();
                foreach (var script in quench.IterationObjectScripts.Concat(quench.IterationAfterTablesObjectScripts))
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(script.LogPath) ?? "");
                    outcomes[folder] = script.Outcome;
                }
                return outcomes;
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    protected void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
