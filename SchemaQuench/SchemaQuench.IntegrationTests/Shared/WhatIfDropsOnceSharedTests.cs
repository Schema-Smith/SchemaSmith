// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// WhatIf previews what the deploy would do. A real run drops a foreign key once -- with the column it is on -- and the
/// later steps find nothing left to drop; WhatIf changes nothing, so every later step that reads the catalog found the
/// key again and reported it again (three times on MySQL in a training lab), while the column step itself left the key
/// out of its preview.
/// </summary>
[NonParallelizable]
public abstract class WhatIfDropsOnceSharedTests : ObjectScriptDeployFixture
{
    protected sealed override string ProductName => "WhatIfDropsOnceProduct";

    /// <summary>The identifier as the engine's packages write it; it also serves in plain SQL.</summary>
    protected abstract string Quote(string name);

    /// <summary>The primary key's name for a table: MySQL and MariaDB call every primary key PRIMARY.</summary>
    protected abstract string PrimaryKeyName(string table);

    private const string Promotion = "wido_promotion";
    private const string Customer = "wido_customer";
    private const string ForeignKey = "fk_wido_customer_promotion";

    [SetUp]
    [OneTimeTearDown]
    public void DropTables()
    {
        Exec($"DROP TABLE IF EXISTS {Quote(Customer)}");
        Exec($"DROP TABLE IF EXISTS {Quote(Promotion)}");
    }

    /// <summary>The newer release declares the promotion table, its column on customer and the key; the older one none of them.</summary>
    private void WriteTables(bool newerRelease)
    {
        WritePackage("");
        if (newerRelease)
            WriteFile("Tables", $"{Promotion}.json", $$"""
                { "Name": "{{Quote(Promotion)}}",
                  "Columns": [ { "Name": "{{Quote("promotion_id")}}", "DataType": "INT" } ],
                  "Indexes": [ { "Name": "{{PrimaryKeyName(Promotion)}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{Quote("promotion_id")}}" } ] }
                """);
        var promotionColumn = newerRelease ? $$""", { "Name": "{{Quote("promotion_id")}}", "DataType": "INT", "Nullable": true }""" : "";
        var foreignKey = newerRelease
            ? $$""", "ForeignKeys": [ { "Name": "{{Quote(ForeignKey)}}", "Columns": "{{Quote("promotion_id")}}", "RelatedTable": "{{Quote(Promotion)}}", "RelatedColumns": "{{Quote("promotion_id")}}" } ]"""
            : "";
        WriteFile("Tables", $"{Customer}.json", $$"""
            { "Name": "{{Quote(Customer)}}",
              "Columns": [ { "Name": "{{Quote("customer_id")}}", "DataType": "INT" }{{promotionColumn}} ],
              "Indexes": [ { "Name": "{{PrimaryKeyName(Customer)}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{Quote("customer_id")}}" } ]{{foreignKey}} }
            """);
    }

    // A real run logs each drop its own way per engine; every line naming the key is about dropping it here.
    private static int Logged(DeployResult run, bool ofItsIndex) =>
        run.Log.Count(l => Names(l) && l.Contains("INDEX", StringComparison.OrdinalIgnoreCase) == ofItsIndex);

    // WhatIf prints the statements it would run, some engines with a message line above each: count the statements.
    private static int Previewed(DeployResult run, bool ofItsIndex) =>
        run.Log.Count(l => Names(l) && (ofItsIndex
            ? l.Contains("DROP INDEX", StringComparison.OrdinalIgnoreCase)
            : !l.Contains("INDEX", StringComparison.OrdinalIgnoreCase)
              && (l.Contains("DROP CONSTRAINT", StringComparison.OrdinalIgnoreCase) || l.Contains("DROP FOREIGN KEY", StringComparison.OrdinalIgnoreCase))));

    private static int Audited(DeployResult run, string action) =>
        run.Audit.Count(r => r.ObjectType == "foreignKey" && r.Action == action && Names(r.ObjectName));

    private static bool Names(string line) => line != null && line.Contains(ForeignKey, StringComparison.OrdinalIgnoreCase);

    private static string Drops(DeployResult run) =>
        string.Join("\n", run.Log.Where(l => l != null && (l.Contains("wido") || l.Contains("Drop") || l.Contains("DROP"))));

    [Test]
    public void AForeignKeyDroppedWithItsColumnAndTable_IsPreviewedOnce_AsTheRealRunDropsIt()
    {
        WriteTables(newerRelease: true);
        Deploy();
        WriteTables(newerRelease: false);

        var preview = Deploy(whatIf: true, dropRemovedTables: true);
        var real = Deploy(dropRemovedTables: true);

        Assert.Multiple(() =>
        {
            Assert.That(Logged(real, ofItsIndex: false), Is.EqualTo(1), "the real run drops the key once:\n" + Drops(real));
            Assert.That(Previewed(preview, ofItsIndex: false), Is.EqualTo(1), "and WhatIf previews it once:\n" + Drops(preview));
            Assert.That(Previewed(preview, ofItsIndex: true), Is.EqualTo(Logged(real, ofItsIndex: true)),
                "the key's index is previewed as often as the real run drops it:\n" + Drops(preview) + "\n--- real:\n" + Drops(real));
            // The summary counts the change audit, so the key is counted once in each, and the real run records its drop.
            Assert.That(Audited(preview, "wouldDrop"), Is.EqualTo(1), "the WhatIf summary counts the key once: " + preview.DescribeAudit());
            Assert.That(Audited(real, "dropped"), Is.EqualTo(1), "the real run records the key's drop once: " + real.DescribeAudit());
        });
    }
}
