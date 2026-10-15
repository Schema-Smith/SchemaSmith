// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// An object script whose text and whose module the server still renders as last applied is not re-run. Every
/// engine: deploy a view, a function, a procedure and a trigger twice; the second deploy runs none of them; a hand
/// change on the server brings that one back; a text change in the package brings that one back; an [ALWAYS] script
/// and the setting turned off run everything. The subclass supplies the engine's DDL.
/// </summary>
[NonParallelizable]
public abstract class UnchangedModuleSkipSharedTests : ObjectScriptDeployFixture
{
    protected sealed override string ProductName => "UnchangedSkipProduct";
    /// <summary>The table JSON the trigger needs, in the engine's table-file shape.</summary>
    protected abstract string TableJson { get; }
    /// <summary>Folder-relative script files: Views/, Functions/, Procedures/, Triggers/.</summary>
    protected abstract Dictionary<string, string> Scripts { get; }
    /// <summary>A statement that changes the deployed view on the server behind SchemaSmith's back.</summary>
    protected abstract string AlterViewByHand { get; }
    /// <summary>The function script with a changed body.</summary>
    protected abstract string ChangedFunctionScript { get; }
    protected abstract string[] DropStatements { get; }

    // Each test starts from no objects: a dropped module renders as nothing, so its stamp cannot match.
    [SetUp]
    public void SetUp()
    {
        foreach (var drop in DropStatements) Exec(drop);
        WritePackage(Scripts);
    }

    [OneTimeTearDown]
    public void DropObjects()
    {
        foreach (var drop in DropStatements) Exec(drop);
    }

    [Test]
    public void SecondDeploy_RunsNoModule_HandChangeAndTextChangeBringTheirScriptBack()
    {
        var first = Deploy().Outcomes;
        Assert.That(first.Values, Is.All.EqualTo(ScriptOutcome.Applied), "first deploy: " + Describe(first));

        var second = Deploy().Outcomes;
        Assert.That(second.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "an unchanged package must not re-run any module: " + Describe(second));

        Exec(AlterViewByHand);
        var third = Deploy().Outcomes;
        Assert.Multiple(() =>
        {
            Assert.That(third["Views"], Is.EqualTo(ScriptOutcome.Applied), "a view changed on the server must be re-applied: " + Describe(third));
            Assert.That(third.Where(kv => kv.Key != "Views").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(third));
        });

        var changed = new Dictionary<string, string>(Scripts) { ["Functions"] = ChangedFunctionScript };
        WritePackage(changed);
        var fourth = Deploy().Outcomes;
        Assert.Multiple(() =>
        {
            Assert.That(fourth["Functions"], Is.EqualTo(ScriptOutcome.Applied), "a function whose text changed must be re-applied: " + Describe(fourth));
            Assert.That(fourth.Where(kv => kv.Key != "Functions").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(fourth));
        });

        var fifth = Deploy().Outcomes;
        Assert.That(fifth.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "the re-applied function must be stamped again: " + Describe(fifth));
    }

    [Test]
    public void AlwaysMarker_Sentinel_RunScriptsTwice_AndTheSettingOff_RunEveryTime()
    {
        WritePackage(Scripts, viewFileName: "v_unchanged [ALWAYS].sql");
        Deploy();
        var second = Deploy().Outcomes;
        Assert.That(second["Views"], Is.EqualTo(ScriptOutcome.Applied), "an [ALWAYS] script runs every time: " + Describe(second));

        var sentinel = new Dictionary<string, string>(Scripts) { ["Views"] = "-- SchemaSmith: AlwaysRun\n" + Scripts["Views"] };
        WritePackage(sentinel);
        Deploy();
        var third = Deploy().Outcomes;
        Assert.Multiple(() =>
        {
            Assert.That(third["Views"], Is.EqualTo(ScriptOutcome.Applied), "a script carrying the AlwaysRun sentinel runs every time: " + Describe(third));
            Assert.That(third.Where(kv => kv.Key != "Views").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(third));
        });

        WritePackage(Scripts);
        Deploy();
        var twice = Deploy(runTwice: true).Outcomes;
        Assert.That(twice.Values, Is.All.EqualTo(ScriptOutcome.Applied), "RunScriptsTwice runs every script: " + Describe(twice));
        var off = Deploy(skipUnchanged: false).Outcomes;
        Assert.That(off.Values, Is.All.EqualTo(ScriptOutcome.Applied), "with SkipUnchangedObjects off every script runs: " + Describe(off));
    }

    [Test]
    public void CustomFolderWithoutObjectType_RunsEveryTime()
    {
        WritePackage(Scripts, customFolder: true);
        Deploy();
        var second = Deploy().Outcomes;
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
        var whatIf = Deploy(whatIf: true).Outcomes;
        Assert.That(whatIf.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "WhatIf after a deploy reports every module unchanged: " + Describe(whatIf));

        Exec(AlterViewByHand);
        var afterHandChange = Deploy(whatIf: true).Outcomes;
        Assert.Multiple(() =>
        {
            Assert.That(afterHandChange["Views"], Is.EqualTo(ScriptOutcome.Applied), "WhatIf would re-apply a view changed on the server: " + Describe(afterHandChange));
            Assert.That(afterHandChange.Where(kv => kv.Key != "Views").Select(kv => kv.Value), Is.All.EqualTo(ScriptOutcome.Unchanged), Describe(afterHandChange));
        });

        var real = Deploy().Outcomes;
        Assert.That(real["Views"], Is.EqualTo(ScriptOutcome.Applied), "WhatIf wrote no stamp, so the real deploy still sees the hand change: " + Describe(real));
    }

    private static string Describe(Dictionary<string, ScriptOutcome> outcomes) => string.Join(", ", outcomes.Select(kv => $"{kv.Key}={kv.Value}"));

    private void WritePackage(Dictionary<string, string> scripts, string viewFileName = "v_unchanged.sql", bool customFolder = false)
    {
        // A folder declared without an ObjectType: the design's opt-out for scripts with side effects.
        var customFolderJson = customFolder ? """, { "FolderPath": "Custom", "QuenchSlot": "Objects" }""" : "";
        WritePackage("""
            { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" }, { "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" },
            { "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }, { "FolderPath": "Triggers", "QuenchSlot": "AfterTablesObjects", "ObjectType": "Triggers" }
            """ + customFolderJson,
            "Views", "Functions", "Procedures", "Triggers", "Custom");
        WriteFile("Tables", "unchanged_t.json", TableJson);
        WriteFile("Views", viewFileName, scripts["Views"]);
        WriteFile("Functions", "f_unchanged.sql", scripts["Functions"]);
        WriteFile("Procedures", "p_unchanged.sql", scripts["Procedures"]);
        WriteFile("Triggers", "tr_unchanged.sql", scripts["Triggers"]);
        if (customFolder) WriteFile("Custom", "p_custom.sql", scripts["Procedures"]);
    }
}
