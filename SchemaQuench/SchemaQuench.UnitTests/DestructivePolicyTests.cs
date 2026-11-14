// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.Utility;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>H3: the destructive-change setting, the acceptance list, and what the summary says.</summary>
[TestFixture]
public class DestructivePolicyTests
{
    private static IConfigurationRoot Config(Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void Unset_IsWarn()
    {
        Assert.That(DestructivePolicy.Read(Config([])).Policy, Is.EqualTo("warn"));
    }

    [TestCase("FAIL", "fail")]
    [TestCase(" allow ", "allow")]
    public void AValueIsReadWithoutCase(string raw, string expected)
    {
        Assert.That(DestructivePolicy.Read(Config(new() { ["Target:DestructiveChangePolicy"] = raw })).Policy, Is.EqualTo(expected));
    }

    // A typo must not disarm a safety setting.
    [TestCase("fial")]
    [TestCase("block")]
    public void AnUnknownValueStopsTheRunNamingTheSetting(string raw)
    {
        var ex = Assert.Throws<RunFailedException>(() => DestructivePolicy.Read(Config(new() { ["Target:DestructiveChangePolicy"] = raw })));
        Assert.That(ex!.Message, Does.Contain("Target:DestructiveChangePolicy").And.Contain(raw));
    }

    [Test]
    public void TheAcceptedNamesAreRenderedForTheProceduresWithoutDelimitersOrCase()
    {
        var policy = DestructivePolicy.Read(Config(new() { ["Target:AcceptDestructive"] = "[Sales].[Orders].[Region]; sales.LegacyOrders , `db`.`T`" }));
        Assert.That(policy.AcceptedForSql, Is.EqualTo(";sales.orders.region;sales.legacyorders;db.t;"));
    }

    [Test]
    public void TheAcceptedNamesCanBeAnArray()
    {
        var policy = DestructivePolicy.Read(Config(new() { ["Target:AcceptDestructive:0"] = "dbo.A", ["Target:AcceptDestructive:1"] = "dbo.B.c" }));
        Assert.That(policy.Accepted, Is.EqualTo(new[] { "dbo.a", "dbo.b.c" }));
    }

    [Test]
    public void TheSummaryListsEachChangeWithItsOutcome_AndOmitsTheSectionWhenThereAreNone()
    {
        var audit = new ChangeAuditCapture();
        audit.Record("column drop", "sales.Orders.Region", "destructive", "refused", new ChangeTarget("db01", "Shop"), estimatedRows: 1204551);
        audit.Record("table drop", "sales.Legacy", "destructive", "accepted", new ChangeTarget("db01", "Shop"));
        audit.Record("column", "sales.Orders.Region", "dropped");

        var json = JObject.Parse(DeploymentSummaryJson.Serialize(Assemble(audit)));
        Assert.Multiple(() =>
        {
            Assert.That((string)json.SelectToken("destructive.policy"), Is.EqualTo("fail"));
            Assert.That((string)json.SelectToken("destructive.changes[0].kind"), Is.EqualTo("columnDrop"));
            Assert.That((string)json.SelectToken("destructive.changes[0].outcome"), Is.EqualTo("refused"));
            Assert.That((long)json.SelectToken("destructive.changes[0].estimatedRows"), Is.EqualTo(1204551));
            Assert.That((string)json.SelectToken("destructive.changes[0].rowSource"), Is.EqualTo("catalog"));
            Assert.That((bool)json.SelectToken("destructive.changes[1].accepted"), Is.True);
            Assert.That((string)json.SelectToken("destructive.changes[1].rowSource"), Is.EqualTo("unknown"));
            Assert.That(((JArray)json.SelectToken("objectChanges.details")!).Count, Is.EqualTo(1), "the destructive rows are not object changes");
        });

        var none = new ChangeAuditCapture();
        none.Record("column", "sales.Orders.Region", "dropped");
        Assert.That(JObject.Parse(DeploymentSummaryJson.Serialize(Assemble(none)))["destructive"]?.Type, Is.EqualTo(JTokenType.Null));
    }

    private static DeploymentSummary Assemble(ChangeAuditCapture audit)
    {
        audit.MarkInstrumented();
        return DeploymentSummaryAssembler.Assemble("P", "SqlServer", "2.9.0", DateTime.UtcNow, DateTime.UtcNow, RunMode.Quench, RunOutcome.Success, 0,
            false, Array.Empty<TargetResult>(), new RunTiming(), Array.Empty<MigrationScriptRun>(), Array.Empty<WhatIfRun>(),
            Array.Empty<FailureRecord>(), 0, audit, destructivePolicy: "fail");
    }
}
