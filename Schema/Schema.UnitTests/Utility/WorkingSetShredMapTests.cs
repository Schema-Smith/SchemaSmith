// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class WorkingSetShredMapTests
{
    [Test]
    public void Parse_FindsEveryShreddedTable_AndNotTheDerivedOne()
    {
        var map = WorkingSetShredMap.Parse(ForgeKindler.GetParseTableJsonScript(Platform.SqlServer));

        foreach (var table in new[] { "#TableDefinitions", "#Columns", "#Indexes", "#XmlIndexes",
                                      "#ForeignKeys", "#CheckConstraints", "#Statistics", "#FullTextIndexes" })
            Assert.That(map.Keys, Does.Contain(table), $"{table} is shredded from JSON and must be mapped.");

        // #Tables is built FROM #TableDefinitions, not from the payload -- it has no shred block, and a
        // client path cannot produce it without the catalog lookups that decide [NewTable].
        Assert.That(map.Keys, Does.Not.Contain("#Tables"),
            "#Tables is derived, not shredded; mapping it would imply the client can produce it.");
    }

    [Test]
    public void Parse_ReadsTheRealMappingForTableDefinitions_NotTheMissingSchemaGuard()
    {
        // The guard directly above the INSERT shreds the same payload with a two-column WITH to report a
        // missing Schema. Anchoring on the INSERT is the only thing telling the two apart, so pin it.
        var columns = WorkingSetShredMap.For(Platform.SqlServer, "#TableDefinitions");

        Assert.That(columns, Has.Count.GreaterThan(30),
            "Picking up the two-column guard instead of the real shred would leave the working set almost empty.");
        Assert.That(columns.Select(c => c.Column), Does.Contain("CompressionType"));
        Assert.That(columns.Select(c => c.Column), Does.Contain("Durability"));
    }

    [Test]
    public void Parse_CarriesNestedChildArraysAsJson()
    {
        var columns = WorkingSetShredMap.For(Platform.SqlServer, "#TableDefinitions");

        // These are the child collections. They must come through as raw JSON text, because the child
        // shreds below read them straight back out of this table.
        foreach (var child in new[] { "Columns", "Indexes", "ForeignKeys", "CheckConstraints", "Statistics" })
            Assert.That(columns.Single(c => c.Column == child).AsJson, Is.True,
                $"{child} carries a nested array and must be mapped AS JSON.");

        Assert.That(columns.Single(c => c.Column == "Schema").AsJson, Is.False,
            "A scalar must not be mapped as JSON.");
    }

    [Test]
    public void Parse_ReadsDottedPathsForTheRebuildPolicyObject()
    {
        var columns = WorkingSetShredMap.For(Platform.SqlServer, "#TableDefinitions");

        // RebuildPolicy is an object, and its fields are read through it. A client writer that assumed
        // column name == top-level property would silently write nulls for all three.
        Assert.That(columns.Single(c => c.Column == "RebuildPolicyMode").JsonPath, Is.EqualTo("$.RebuildPolicy.Mode"));
        Assert.That(columns.Single(c => c.Column == "RebuildPolicyThreshold").JsonPath, Is.EqualTo("$.RebuildPolicy.Threshold"));
    }

    [Test]
    public void Parse_IgnoresShapesLeftBehindInComments()
    {
        var script = @"
INSERT INTO #Probe ([Kept])
SELECT [Kept]
  FROM OPENJSON(@Payload) WITH (
    [Kept] NVARCHAR(50) '$.Kept',
    -- [Removed] NVARCHAR(50) '$.Removed',
    [AlsoKept] BIT '$.AlsoKept'
  ) t;";

        var columns = WorkingSetShredMap.Parse(script)["#Probe"];

        Assert.That(columns.Select(c => c.Column), Is.EquivalentTo(new[] { "Kept", "AlsoKept" }),
            "A column left behind in a comment would be written into a table that no longer has it.");
    }

    [Test]
    public void Parse_EveryPlatformWithAJsonShredIsReadable()
    {
        // PostgreSQL and MySQL shred with their own syntax, not OPENJSON, so they are not expected here.
        // This pins the fact that SQL Server's script stays readable -- the mapping is only drift-proof
        // for as long as it can still be read.
        Assert.That(WorkingSetShredMap.Parse(ForgeKindler.GetParseTableJsonScript(Platform.SqlServer)),
            Is.Not.Empty);
    }
}
