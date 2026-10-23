// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        Exec(DropOverView);
        foreach (var drop in DropStatements) Exec(drop);
        WritePackage(Scripts);
    }

    [OneTimeTearDown]
    public void DropObjects()
    {
        Exec(DropOverView);
        foreach (var drop in DropStatements) Exec(drop);
    }

    // A target database's connect that failed said only the provider's text -- "The operation has timed out" for a stall --
    // under "Begin Quench", naming neither the target nor that it was the connect. A refused login fails at the same open.
    [Test]
    public void AFailedConnect_NamesTheEngineTheTargetAndThatItWasTheConnect()
    {
        var result = Deploy(expectSuccess: false, login: ("schemasmith_no_such_user", "not-the-password"));
        var expected = $"Unable to connect to {Platform.ToDisplayName()} at 127.0.0.1 [Database: {MainDb}] with user schemasmith_no_such_user: ";
        Assert.That(result.Log.Where(l => l != null && l.StartsWith("ERROR")), Has.Some.Contains(expected),
            string.Join("\n", result.Log.Where(l => l != null && l.StartsWith("ERROR"))));
    }

    [Test]
    public void SecondDeploy_RunsNoModule_HandChangeAndTextChangeBringTheirScriptBack()
    {
        var first = Deploy().Outcomes;
        Assert.That(first.Values, Is.All.EqualTo(ScriptOutcome.Applied), "first deploy: " + Describe(first));

        var secondRun = Deploy();
        var second = secondRun.Outcomes;
        Assert.Multiple(() =>
        {
            Assert.That(second.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "an unchanged package must not re-run any module: " + Describe(second));
            // The documented line, on an ordinary deploy: it was written only on the retry pass, which a skipped script never reaches.
            foreach (var folder in second.Keys)
                Assert.That(secondRun.Log.Any(l => l != null && l.Contains("Unchanged, not re-run:") && l.Contains(folder)), Is.True,
                    $"the {folder} script says it was not re-run; log: " + string.Join(" | ", secondRun.Log));
        });

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

    // A view takes its table's columns when it is created, so its text and its rendering stayed as stamped while the
    // table grew: a SELECT * view skipped as unchanged never showed a column the same deploy added.
    [Test]
    public void ASelectStarView_PicksUpAColumnTheSameDeployAddsToItsTable_AndIsStampedAgain()
    {
        var starView = Scripts["Views"].Replace("SELECT x FROM", "SELECT * FROM").Replace("select `x` from", "select * from");
        Assert.That(starView, Is.Not.EqualTo(Scripts["Views"]), "setup: the view must read every column");
        var scripts = new Dictionary<string, string>(Scripts) { ["Views"] = starView };

        WritePackage(scripts);
        Deploy();
        Assert.That(Deploy().Outcomes["Views"], Is.EqualTo(ScriptOutcome.Unchanged), "setup: the view is stamped");
        Assert.That(ViewColumns("v_unchanged"), Is.EqualTo(2), "setup");

        WritePackage(scripts, tableJson: WidenedTableJson());
        var third = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(ViewColumns("v_unchanged"), Is.EqualTo(3), "the view shows the column its table gained: " + third.Describe());
            Assert.That(third.Outcomes["Views"], Is.EqualTo(ScriptOutcome.Applied));
        });

        var fourth = Deploy().Outcomes;
        Assert.That(fourth.Values, Is.All.EqualTo(ScriptOutcome.Unchanged), "the re-run view is stamped again: " + Describe(fourth));
    }

    // A view over that view keeps the old column list too, though its text may name no table the deploy changed. One that
    // also reads the table is found with the view it reads, and it sorts first: it must still be re-created after it.
    [TestCase(false)]
    [TestCase(true)]
    public void AViewOverASelectStarView_PicksUpTheColumnToo(bool readsTheTableToo)
    {
        var starView = Scripts["Views"].Replace("SELECT x FROM", "SELECT * FROM").Replace("select `x` from", "select * from");
        var tableRef = Platform switch { Platform.SqlServer => "dbo.unchanged_t", Platform.PostgreSQL => "public.unchanged_t", _ => "`unchanged_t`" };
        var viewRef = tableRef.Replace("unchanged_t", "v_unchanged");
        var overView = Regex.Replace(starView.Replace("v_unchanged", "v_unchanged_over"), @"\* FROM " + Regex.Escape(tableRef),
            $"v.* FROM {viewRef} v" + (readsTheTableToo ? $" WHERE EXISTS (SELECT 1 FROM {tableRef})" : ""), RegexOptions.IgnoreCase);
        Assert.That(overView, Does.Contain($"FROM {viewRef} v"), "setup: the second view reads the first");
        var scripts = new Dictionary<string, string>(Scripts) { ["Views"] = starView };

        WritePackage(scripts);
        WriteFile("Views", "v_unchanged_over.sql", overView);
        Deploy();
        Deploy();
        Assert.That(ViewColumns("v_unchanged_over"), Is.EqualTo(2), "setup");

        WritePackage(scripts, tableJson: WidenedTableJson());
        WriteFile("Views", "v_unchanged_over.sql", overView);
        var third = Deploy();
        Assert.That(ViewColumns("v_unchanged_over"), Is.EqualTo(3), "the view over the view shows the column too: " + third.Describe());
    }

    private string WidenedTableJson()
    {
        var widened = Regex.Replace(TableJson, @"\{ ""Name"": ""(\[?`?)w(\]?`?)"", ""DataType"": ""([^""]+)"", ""Nullable"": true \}",
            m => m.Value + $@", {{ ""Name"": ""{m.Groups[1].Value}z{m.Groups[2].Value}"", ""DataType"": ""{m.Groups[3].Value}"", ""Nullable"": true }}");
        Assert.That(widened, Is.Not.EqualTo(TableJson), "setup: the table gains a column");
        return widened;
    }

    private long ViewColumns(string view)
    {
        var schema = Platform switch { Platform.SqlServer => "dbo", Platform.PostgreSQL => "public", _ => MainDb };
        return Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{view}'");
    }

    private string DropOverView => Platform switch
    {
        Platform.SqlServer => "DROP VIEW IF EXISTS [dbo].[v_unchanged_over]",
        Platform.PostgreSQL => "DROP VIEW IF EXISTS public.v_unchanged_over",
        _ => "DROP VIEW IF EXISTS `v_unchanged_over`"
    };

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

    private void WritePackage(Dictionary<string, string> scripts, string viewFileName = "v_unchanged.sql", bool customFolder = false, string tableJson = null)
    {
        // A folder declared without an ObjectType: the design's opt-out for scripts with side effects.
        var customFolderJson = customFolder ? """, { "FolderPath": "Custom", "QuenchSlot": "Objects" }""" : "";
        WritePackage("""
            { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" }, { "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" },
            { "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }, { "FolderPath": "Triggers", "QuenchSlot": "AfterTablesObjects", "ObjectType": "Triggers" }
            """ + customFolderJson,
            "Views", "Functions", "Procedures", "Triggers", "Custom");
        WriteFile("Tables", "unchanged_t.json", tableJson ?? TableJson);
        WriteFile("Views", viewFileName, scripts["Views"]);
        WriteFile("Functions", "f_unchanged.sql", scripts["Functions"]);
        WriteFile("Procedures", "p_unchanged.sql", scripts["Procedures"]);
        WriteFile("Triggers", "tr_unchanged.sql", scripts["Triggers"]);
        if (customFolder) WriteFile("Custom", "p_custom.sql", scripts["Procedures"]);
    }
}
