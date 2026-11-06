// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Schema.Utility;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>
/// H1 drift: what counts as drift, how --DriftFailOn narrows it, how targets with the same plan group, and the exit code a
/// pipeline acts on.
/// </summary>
[TestFixture]
public class DriftTests
{
    private static readonly ChangeTarget Db1 = new("srv", "Shop");

    private static DriftUnit Unit(string database = "Shop", string schema = "", string template = "Main", bool failed = false) =>
        new("srv", database, schema, template, failed, false);

    private static ChangeAuditRow Row(string type, string name, string action, ChangeTarget target = null) =>
        new(type, name, action, Target: target ?? Db1);

    private static DriftReport Decide(IEnumerable<DriftUnit> units, IEnumerable<ChangeAuditRow> rows, DriftCapture capture = null, string failOn = null) =>
        DriftVerdict.Decide(units.ToList(), rows.ToList(), capture ?? new DriftCapture(), DriftFailOn.Parse(failOn));

    [Test]
    public void ATargetWhosePreviewChangesNothing_IsClean()
    {
        var report = Decide([Unit()], [Row("procedure", "Procedures/Get.sql", "unchanged")]);
        Assert.Multiple(() =>
        {
            Assert.That(report.Drifted, Is.False);
            Assert.That(report.Targets.Single().Status, Is.EqualTo("clean"));
        });
    }

    [TestCase("wouldCreate")]
    [TestCase("wouldModify")]
    [TestCase("wouldRebuild")]
    [TestCase("wouldChange")]
    [TestCase("wouldDrop")]
    public void AnyChangeTheDeployWouldMake_IsDrift(string action)
    {
        var report = Decide([Unit()], [Row("column", "dbo.Orders.Region", action)]);
        Assert.Multiple(() =>
        {
            Assert.That(report.Drifted, Is.True);
            Assert.That(report.Targets.Single().Status, Is.EqualTo("drifted"));
            Assert.That(report.Targets.Single().Counted, Is.EqualTo(1));
        });
    }

    // Data delivery runs every deploy by design, a refused or downgraded change says nothing about state, and the grants a
    // re-applied module gets back follow the module, which counts on its own.
    [TestCase("wouldDeliver")]
    [TestCase("downgraded")]
    [TestCase("unchanged")]
    [TestCase("wouldReapply")]
    [TestCase("wouldResign")]
    public void ARowThatSaysNothingAboutState_IsNotDrift(string action)
    {
        Assert.That(Decide([Unit()], [Row("table", "dbo.Orders", action)]).Drifted, Is.False);
    }

    [Test]
    public void ADestructiveRow_IsNotCountedTwice()
    {
        var report = Decide([Unit()], [Row("column", "dbo.Orders.Old", "wouldDrop"), Row("column drop", "dbo.Orders.Old", "destructive")]);
        Assert.That(report.Targets.Single().Counted, Is.EqualTo(1));
    }

    // PreventDrop kept it, so a deploy would not change it: reported, not counted.
    [Test]
    public void ADropPreventDropSuppressed_IsExtraNotDrift()
    {
        var report = Decide([Unit()], [Row("table", "dbo.Legacy", "dropSuppressed")]);
        Assert.Multiple(() =>
        {
            Assert.That(report.Drifted, Is.False);
            Assert.That(report.Extra.Single().ObjectName, Is.EqualTo("dbo.Legacy"));
        });
    }

    [Test]
    public void OnlyTheRowsOfATarget_CountForIt()
    {
        var other = new ChangeTarget("srv", "Other");
        var report = Decide([Unit(), Unit("Other")], [Row("table", "dbo.T", "wouldCreate", other)]);
        Assert.Multiple(() =>
        {
            Assert.That(report.Targets.Single(t => t.Database == "Shop").Status, Is.EqualTo("clean"));
            Assert.That(report.Targets.Single(t => t.Database == "Other").Status, Is.EqualTo("drifted"));
        });
    }

    [Test]
    public void AFailedTarget_IsFailedWhateverItRecorded()
    {
        var report = Decide([Unit(failed: true)], [Row("table", "dbo.T", "wouldCreate")]);
        Assert.That(report.Targets.Single().Status, Is.EqualTo("failed"));
    }

    [Test]
    public void AModuleTheProbeSaysChanged_IsDrift()
    {
        var capture = new DriftCapture();
        capture.RecordModule(Db1, "Procedures/Get.sql", DriftModuleState.WouldApply);
        var report = Decide([Unit()], [], capture);
        Assert.That(report.Targets.Single().Status, Is.EqualTo("drifted"));
    }

    // A module that is never stamped always "would apply", so it cannot be compared.
    [Test]
    public void AModuleThatCannotBeCompared_IsUnverifiableNotDrift()
    {
        var capture = new DriftCapture();
        capture.RecordModule(Db1, "Procedures/Get.sql", DriftModuleState.Unverifiable);
        var report = Decide([Unit()], [], capture);
        Assert.Multiple(() =>
        {
            Assert.That(report.Drifted, Is.False);
            Assert.That(report.Targets.Single().Status, Is.EqualTo("unverifiable"));
            Assert.That(report.Unverifiable.Single().ObjectName, Is.EqualTo("Procedures/Get.sql"));
        });
    }

    [Test]
    public void APendingMigration_IsReportedButNotCountedByDefault()
    {
        var capture = new DriftCapture();
        capture.RecordMigration(Db1, "Before/AddRegion.sql", "Before");
        var report = Decide([Unit()], [], capture);
        Assert.Multiple(() =>
        {
            Assert.That(report.Drifted, Is.False);
            Assert.That(report.PendingMigrations.Single().ObjectName, Is.EqualTo("Before/AddRegion.sql"));
        });
    }

    [Test]
    public void APendingMigration_CountsWhenFailOnNamesMigrations()
    {
        var capture = new DriftCapture();
        capture.RecordMigration(Db1, "Before/AddRegion.sql", "Before");
        Assert.That(Decide([Unit()], [], capture, "migrations").Drifted, Is.True);
    }

    [Test]
    public void TheDefaultFailOn_IsEveryActionAndModules()
    {
        Assert.That(DriftFailOn.Parse(null).Tokens, Is.EqualTo(new[] { "create", "modify", "drop", "modules" }));
    }

    [TestCase("drop", "column", "wouldModify", false)]
    [TestCase("drop", "column", "wouldDrop", true)]
    [TestCase("index", "column", "wouldDrop", false)]
    [TestCase("index", "index", "wouldCreate", true)]
    [TestCase("create,foreignKey", "foreign key", "wouldCreate", true)]
    [TestCase("create,foreignKey", "foreign key", "wouldDrop", false)]
    [TestCase("Domain Type", "domain type", "wouldModify", true)]
    public void FailOn_CountsARowWhenItsActionAndItsTypeAreBothListed(string failOn, string type, string action, bool counts)
    {
        Assert.That(Decide([Unit()], [Row(type, "x", action)], failOn: failOn).Drifted, Is.EqualTo(counts));
    }

    [Test]
    public void FailOn_ThatNamesATypeButNotModules_LeavesModulesOut()
    {
        var capture = new DriftCapture();
        capture.RecordModule(Db1, "Procedures/Get.sql", DriftModuleState.WouldApply);
        Assert.That(Decide([Unit()], [], capture, "table").Drifted, Is.False);
    }

    [Test]
    public void AnUnknownFailOnToken_StopsTheRunNamingIt()
    {
        var ex = Assert.Throws<RunFailedException>(() => DriftFailOn.Parse("create,colum"));
        Assert.That(ex!.Message, Does.Contain("colum").And.Contain("--DriftFailOn"));
    }

    // Tenants of one template with the same change make one group: the schema in each object name is not part of the plan.
    [Test]
    public void TargetsOfOneTemplateWithTheSamePlan_FormOneGroup()
    {
        var acme = new ChangeTarget("srv", "Shop", "t_acme");
        var bolt = new ChangeTarget("srv", "Shop", "t_bolt");
        var zeta = new ChangeTarget("srv", "Shop", "t_zeta");
        var report = Decide(
            [Unit(schema: "t_acme", template: "Tenant"), Unit(schema: "t_bolt", template: "Tenant"), Unit(schema: "t_zeta", template: "Tenant")],
            [Row("column", "t_acme.Orders.Region", "wouldModify", acme), Row("column", "t_bolt.Orders.Region", "wouldModify", bolt),
             Row("table", "t_zeta.Extra", "wouldCreate", zeta)]);
        Assert.Multiple(() =>
        {
            Assert.That(report.PlanGroups.Select(g => g.Targets).OrderByDescending(n => n), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(report.PlanGroups.Single(g => g.Targets == 2).Sample, Is.EqualTo("srv.Shop.t_acme"));
            Assert.That(report.Targets.Single(t => t.Schema == "t_acme").Plan, Is.EqualTo(report.Targets.Single(t => t.Schema == "t_bolt").Plan));
        });
    }

    [TestCase(false, false, false, 0)]
    [TestCase(false, false, true, 1)]
    [TestCase(false, true, true, 5)]
    [TestCase(true, true, true, 2)]
    [TestCase(true, false, false, 2)]
    public void TheExitCode_PutsAFailureBeforeDriftBeforeAnIncompleteRun(bool failed, bool drifted, bool incomplete, int expected)
    {
        Assert.That(DriftVerdict.ExitCode(failed, drifted, incomplete), Is.EqualTo(expected));
    }

    private static DeploymentSummary Summary(RunMode mode, DriftReport drift) =>
        DeploymentSummaryAssembler.Assemble("P", "SqlServer", "2.9.0", DateTime.UtcNow, DateTime.UtcNow, mode, RunOutcome.Success, 5,
            false, Array.Empty<TargetResult>(), new RunTiming(), Array.Empty<MigrationScriptRun>(), Array.Empty<WhatIfRun>(),
            Array.Empty<FailureRecord>(), 0, new ChangeAuditCapture(), drift: drift, driftTier: 1);

    [Test]
    public void TheSummaryOfADriftRun_CarriesTheVerdictAndTheTier()
    {
        var report = Decide([Unit()], [Row("column", "dbo.Orders.Region", "wouldModify")]);
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(Summary(RunMode.Drift, report)));
        Assert.Multiple(() =>
        {
            Assert.That((string)json.SelectToken("run.mode"), Is.EqualTo("Drift"));
            Assert.That((int?)json.SelectToken("run.driftTier"), Is.EqualTo(1));
            Assert.That((bool?)json.SelectToken("drift.drifted"), Is.True);
            Assert.That((string)json.SelectToken("drift.targets[0].changes[0].objectName"), Is.EqualTo("dbo.Orders.Region"));
            Assert.That(DeploymentSummaryText.Render(Summary(RunMode.Drift, report)), Does.Contain("## Drift").And.Contain("Drifted: yes"));
        });
    }

    [Test]
    public void TheSummaryOfAnyOtherRun_HasNoDriftBlock()
    {
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(Summary(RunMode.WhatIf, null)));
        Assert.Multiple(() =>
        {
            Assert.That(json.SelectToken("drift")?.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json.SelectToken("run.driftTier")?.Type, Is.EqualTo(JTokenType.Null));
        });
    }

    [Test]
    public void TheConsole_SaysOneLinePerPlan_AndTheAnswer()
    {
        var acme = new ChangeTarget("srv", "Shop", "t_acme");
        var bolt = new ChangeTarget("srv", "Shop", "t_bolt");
        var report = Decide([Unit(schema: "t_acme", template: "Tenant"), Unit(schema: "t_bolt", template: "Tenant"), Unit(schema: "t_zeta", template: "Tenant")],
            [Row("column", "t_acme.Orders.Region", "wouldModify", acme), Row("column", "t_bolt.Orders.Region", "wouldModify", bolt)]);
        var lines = DriftVerdict.ConsoleLines(report).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Some.Contains("Tenant: drift").And.Some.Contains("on 2 of 3 target(s)"));
            Assert.That(lines, Has.Some.Contains("Tenant: no drift on 1 of 3 target(s), e.g. srv.Shop.t_zeta"));
            Assert.That(lines.Last(), Is.EqualTo("Drift found on 2 of 3 target(s)"));
        });
    }

    // Tier 2: the helpers' routines and tables are named in the helper database, nothing else is.
    [TestCase("CALL SchemaSmith_ModifiedTableQuench('P', 'shop', 1)", "CALL `drift`.SchemaSmith_ModifiedTableQuench('P', 'shop', 1)")]
    [TestCase("SELECT TextHash FROM SchemaSmith_ScriptStamp WHERE PathHash = 'x'", "SELECT TextHash FROM `drift`.SchemaSmith_ScriptStamp WHERE PathHash = 'x'")]
    [TestCase("DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Tables", "DROP TEMPORARY TABLE IF EXISTS `drift`._SchemaSmith_Tables")]
    [TestCase("SELECT SchemaSmith_IdentifierKey(TABLE_NAME) FROM information_schema.TABLES", "SELECT `drift`.SchemaSmith_IdentifierKey(TABLE_NAME) FROM information_schema.TABLES")]
    [TestCase("SELECT 1 FROM information_schema.TABLES WHERE TABLE_NAME = 'SchemaSmith_KindleStamp'", "SELECT 1 FROM information_schema.TABLES WHERE TABLE_NAME = 'SchemaSmith_KindleStamp'")]
    [TestCase("INSERT INTO `drift`.SchemaSmith_ExpressionMap SELECT * FROM `shop`.SchemaSmith_ExpressionMap", "INSERT INTO `drift`.SchemaSmith_ExpressionMap SELECT * FROM `shop`.SchemaSmith_ExpressionMap")]
    [TestCase("SET @schemasmith_destructive_policy = 'fail'", "SET @schemasmith_destructive_policy = 'fail'")]
    [TestCase("SELECT * FROM shop.Orders", "SELECT * FROM shop.Orders")]
    public void TheHelperDatabase_QualifiesOnlySchemaSmithNames(string sql, string expected)
    {
        Assert.That(DriftHelperDatabase.Qualify(sql, "drift"), Is.EqualTo(expected));
    }

    [Test]
    public void OnMySqlWithKindlingOn_AHelperDatabaseChoosesTier2_AndNoneIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DriftTiers.Choose(Schema.Domain.Platform.MySQL, kindlingOff: false, "drift"), Is.EqualTo(2));
            Assert.That(DriftTiers.Choose(Schema.Domain.Platform.MariaDb, kindlingOff: true, "drift"), Is.EqualTo(0));
            Assert.That(DriftTiers.Choose(Schema.Domain.Platform.PostgreSQL, kindlingOff: false, null), Is.EqualTo(1));
            Assert.Throws<RunFailedException>(() => DriftTiers.Choose(Schema.Domain.Platform.MariaDb, kindlingOff: false, " "));
        });
    }
}
