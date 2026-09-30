// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Domain.PostgreSQL;
using Schema.Domain.MySQL;
using Schema.Utility;
using Schema.Delivery;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class ImportTableHelperTests
{
    [Test]
    public void PreserveDataDeliveryAndCustomProperties_CopiesDataDeliveryFields()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            DataDelivery = [new DataDelivery { ContentFile = "orders.csv", MergeType = "Insert/Update", MatchColumns = "OrderId", MergeFilter = "Active = 1", MergeDisableTriggers = true }],
            OldName = "OldOrders"
        };

        var newTable = new SqlServerTable { Name = "Orders" };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newTable.DataDelivery[0].ContentFile, Is.EqualTo("orders.csv"));
        Assert.That(newTable.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
        Assert.That(newTable.DataDelivery[0].MatchColumns, Is.EqualTo("OrderId"));
        Assert.That(newTable.DataDelivery[0].MergeFilter, Is.EqualTo("Active = 1"));
        Assert.That(newTable.DataDelivery[0].MergeDisableTriggers, Is.True);
        Assert.That(newTable.OldName, Is.EqualTo("OldOrders"));
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_DefaultsMergeTypeToNone()
    {
        var original = new SqlServerTable { Name = "Orders", DataDelivery = [new DataDelivery { MergeType = null }] };
        var newTable = new SqlServerTable { Name = "Orders" };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newTable.DataDelivery[0].MergeType, Is.EqualTo("None"));
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_CopiesDynamicProperties()
    {
        var original = new SqlServerTable { Name = "Orders" };
        original.Extensions = new JObject { ["CustomProp"] = "CustomValue" };

        var newTable = new SqlServerTable { Name = "Orders" };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newTable.Extensions?["CustomProp"]?.ToString(), Is.EqualTo("CustomValue"));
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_CopiesColumnDynamicProperties()
    {
        var originalCol = new Column { Name = "OrderId" };
        originalCol.Extensions = new JObject { ["IsAudited"] = true };

        var original = new SqlServerTable { Name = "Orders", Columns = { originalCol } };
        var newCol = new Column { Name = "OrderId" };
        var newTable = new SqlServerTable { Name = "Orders", Columns = { newCol } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newCol.Extensions?["IsAudited"]?.Value<bool>(), Is.True);
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_MatchesByOldName()
    {
        var originalCol = new Column { Name = "OldColName", OldName = "OldColName" };
        originalCol.Extensions = new JObject { ["Custom"] = "value" };

        var original = new SqlServerTable { Name = "Orders", Columns = { originalCol } };

        // The new column's current name matches the original's OldName
        var newCol = new Column { Name = "OldColName" };
        var newTable = new SqlServerTable { Name = "Orders", Columns = { newCol } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newCol.Extensions?["Custom"]?.ToString(), Is.EqualTo("value"));
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_PostgreSQL_CopiesPlatformSpecific()
    {
        var original = new PostgreSqlTable
        {
            Name = "orders",
            DataDelivery = [new PostgreSqlDataDelivery { MergeDisableRules = true, MergeUpdateDescendents = true }]
        };

        var newTable = new PostgreSqlTable { Name = "orders" };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newTable.DataDelivery[0].MergeDisableRules, Is.True);
        Assert.That(newTable.DataDelivery[0].MergeUpdateDescendents, Is.True);
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_MySQL_CopiesFullTextIndexDynamicProps()
    {
        var originalFti = new Schema.Domain.MySQL.FullTextIndex { Name = "fti_content" };
        originalFti.Extensions = new JObject { ["CustomParser"] = "ngram" };

        var original = new MySqlTable { Name = "articles", FullTextIndexes = { originalFti } };

        var newFti = new Schema.Domain.MySQL.FullTextIndex { Name = "fti_content" };
        var newTable = new MySqlTable { Name = "articles", FullTextIndexes = { newFti } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newFti.Extensions?["CustomParser"]?.ToString(), Is.EqualTo("ngram"));
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_SqlServer_CopiesXmlIndexDynamicProps()
    {
        var originalXml = new XmlIndex { Name = "XI_Data" };
        originalXml.Extensions = new JObject { ["CustomFlag"] = "yes" };

        var original = new SqlServerTable { Name = "Docs", XmlIndexes = { originalXml } };

        var newXml = new XmlIndex { Name = "XI_Data" };
        var newTable = new SqlServerTable { Name = "Docs", XmlIndexes = { newXml } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newXml.Extensions?["CustomFlag"]?.ToString(), Is.EqualTo("yes"));
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_HandlesNullContentFile()
    {
        var original = new SqlServerTable { Name = "Orders", DataDelivery = null };
        var newTable = new SqlServerTable { Name = "Orders" };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        // Table.DataDelivery defaults to an empty list (not null); with no original deliveries to
        // preserve, the freshly-imported table simply keeps its own (empty) list.
        Assert.That(newTable.DataDelivery, Is.Empty);
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_MatchesColumnsIgnoringQuotes()
    {
        var originalCol = new Column { Name = "[OrderId]" };
        originalCol.Extensions = new JObject { ["IsKey"] = true };

        var original = new SqlServerTable { Name = "Orders", Columns = { originalCol } };

        // New column with brackets
        var newCol = new Column { Name = "[OrderId]" };
        var newTable = new SqlServerTable { Name = "Orders", Columns = { newCol } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newCol.Extensions?["IsKey"]?.Value<bool>(), Is.True);
    }

    [Test]
    public void PreserveCustomProperties_MultiVariantFullTextIndex_AttributesExtractedToActiveVariant()
    {
        var original = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex =
            [
                new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[A]", KeyIndex = "[PK]", Columns = "[T]", ShouldApplyExpression = "DB_NAME() = 'East'" },
                new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[B]", KeyIndex = "[PK]", Columns = "[T]", ShouldApplyExpression = "DB_NAME() = 'West'" }
            ]
        };
        var reimported = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = [new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[Extracted]", KeyIndex = "[PK]", Columns = "[T]" }]
        };

        // East is active on this source — the extracted FT refreshes the East variant; West is preserved.
        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, expr => expr == "DB_NAME() = 'East'");

        Assert.That(reimported.FullTextIndex, Has.Count.EqualTo(2));
        Assert.That(reimported.FullTextIndex[0].ShouldApplyExpression, Is.EqualTo("DB_NAME() = 'East'"));
        Assert.That(reimported.FullTextIndex[0].FullTextCatalog, Is.EqualTo("[Extracted]"));
        Assert.That(reimported.FullTextIndex[1].ShouldApplyExpression, Is.EqualTo("DB_NAME() = 'West'"));
    }

    [Test]
    public void PreserveCustomProperties_SingleFullTextIndex_CarriesShouldApplyExpressionOntoReimport()
    {
        var original = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = [new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[OldCatalog]", KeyIndex = "[PK]", Columns = "[T]", ShouldApplyExpression = "1 = 1" }]
        };
        var reimported = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = [new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[NewCatalog]", KeyIndex = "[PK]", Columns = "[T]" }]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.FullTextIndex[0].ShouldApplyExpression, Is.EqualTo("1 = 1"));
        Assert.That(reimported.FullTextIndex[0].FullTextCatalog, Is.EqualTo("[NewCatalog]"));
    }

    [Test]
    public void PreserveCustomProperties_GatedSingleFullTextIndex_SurvivesReimportWhenGatedOutOnSource()
    {
        var original = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = [new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[FtCatalog]", KeyIndex = "[PK]", Columns = "[T]", ShouldApplyExpression = "DB_NAME() = 'Prod'" }]
        };
        var reimported = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = []
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.FullTextIndex, Has.Count.EqualTo(1));
        Assert.That(reimported.FullTextIndex[0].ShouldApplyExpression, Is.EqualTo("DB_NAME() = 'Prod'"));
    }

    [Test]
    public void PreserveCustomProperties_UngatedSingleFullTextIndex_NotResurrectedWhenAbsentOnSource()
    {
        var original = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = [new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[FtCatalog]", KeyIndex = "[PK]", Columns = "[T]" }]
        };
        var reimported = new SqlServerTable
        {
            Name = "[Docs]",
            FullTextIndex = []
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.FullTextIndex, Is.Empty);
    }

    [Test]
    public void PreserveDataDeliveryAndCustomProperties_CopiesVariantName()
    {
        var originalCol = new Column { Name = "payload", ShouldApplyExpression = "1=1", VariantName = "Modern engines" };
        var original = new SqlServerTable { Name = "Orders", Columns = { originalCol } };
        var newCol = new Column { Name = "payload" };
        var newTable = new SqlServerTable { Name = "Orders", Columns = { newCol } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, original, _ => true);

        Assert.That(newCol.VariantName, Is.EqualTo("Modern engines"));
    }

    [Test]
    public void PreserveCustomProperties_MultiVariantNamedComponent_RefreshesActiveVariantWithExtractedShape()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes =
            [
                new Index { Name = "IX_Region", IndexColumns = "RegionId", ShouldApplyExpression = "DB_NAME() = 'East'", VariantName = "East" },
                new Index { Name = "IX_Region", IndexColumns = "RegionId, Zone", ShouldApplyExpression = "DB_NAME() = 'West'", VariantName = "West" }
            ]
        };
        var reimported = new SqlServerTable
        {
            Name = "Orders",
            Indexes = [new Index { Name = "IX_Region", IndexColumns = "RegionId, Refreshed" }]
        };

        // East is active — the extracted index refreshes the East variant (keeping its gate/label); West is preserved.
        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, expr => expr == "DB_NAME() = 'East'");

        Assert.That(reimported.Indexes, Has.Count.EqualTo(2));
        Assert.That(reimported.Indexes[0].VariantName, Is.EqualTo("East"));
        Assert.That(reimported.Indexes[0].IndexColumns, Is.EqualTo("RegionId, Refreshed")); // extracted shape, not authored
        Assert.That(reimported.Indexes[1].VariantName, Is.EqualTo("West"));
        Assert.That(reimported.Indexes[1].IndexColumns, Is.EqualTo("RegionId, Zone")); // inactive variant preserved
    }

    [Test]
    public void PreserveCustomProperties_MultiVariantGroup_DoesNotDisturbOtherComponents()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes =
            [
                new Index { Name = "IX_Region", IndexColumns = "RegionId", ShouldApplyExpression = "DB_NAME() = 'East'" },
                new Index { Name = "IX_Region", IndexColumns = "RegionId, Zone", ShouldApplyExpression = "DB_NAME() = 'West'" },
                new Index { Name = "IX_Plain", IndexColumns = "OrderDate" }
            ]
        };
        var extractedPlain = new Index { Name = "IX_Plain", IndexColumns = "OrderDate, Status" };
        var reimported = new SqlServerTable
        {
            Name = "Orders",
            Indexes = [new Index { Name = "IX_Region", IndexColumns = "RegionId" }, extractedPlain]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, expr => expr == "DB_NAME() = 'East'");

        Assert.That(reimported.Indexes, Has.Count.EqualTo(3));
        Assert.That(reimported.Indexes.Single(i => i.Name == "IX_Plain").IndexColumns, Is.EqualTo("OrderDate, Status"));
    }

    [Test]
    public void PreserveCustomProperties_MultiVariantGroup_AbsentFromExtraction_SurvivesWholesale()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes =
            [
                new Index { Name = "IX_Gated", IndexColumns = "RegionId", ShouldApplyExpression = "DB_NAME() = 'East'", VariantName = "East" },
                new Index { Name = "IX_Gated", IndexColumns = "RegionId, Zone", ShouldApplyExpression = "DB_NAME() = 'West'", VariantName = "West" }
            ]
        };
        var reimported = new SqlServerTable { Name = "Orders", Indexes = [] };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.Indexes, Has.Count.EqualTo(2));
        Assert.That(reimported.Indexes.Select(i => i.VariantName), Is.EquivalentTo(new[] { "East", "West" }));
    }

    [Test]
    public void PreserveCustomProperties_MultiVariantColumnGroup_PreservedMidListWithCopyOldName()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Columns =
            [
                new Column { Name = "Id", DataType = "INT" },
                new Column { Name = "payload", DataType = "JSON", ShouldApplyExpression = "1=1", VariantName = "Modern" },
                new Column { Name = "payload", DataType = "NVARCHAR(MAX)", ShouldApplyExpression = "1=0", VariantName = "Legacy" }
            ]
        };
        var reimported = new SqlServerTable
        {
            Name = "Orders",
            Columns =
            [
                new Column { Name = "Id", DataType = "INT" },
                new Column { Name = "payload", DataType = "JSON" }
            ]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, expr => expr == "1=1");

        Assert.That(reimported.Columns, Has.Count.EqualTo(3));
        Assert.That(reimported.Columns[0].Name.Trim('[', ']', '"', '`'), Is.EqualTo("Id"));
        Assert.That(reimported.Columns.Where(c => c.Name.Trim('[', ']', '"', '`') == "payload").Select(c => c.VariantName),
                    Is.EquivalentTo(new[] { "Modern", "Legacy" }));
    }

    [Test]
    public void PreserveCustomProperties_NoVariantActive_AppendsExtractedUngated()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes =
            [
                new Index { Name = "IX_Region", IndexColumns = "RegionId", ShouldApplyExpression = "DB_NAME() = 'East'", VariantName = "East" },
                new Index { Name = "IX_Region", IndexColumns = "RegionId, Zone", ShouldApplyExpression = "DB_NAME() = 'West'", VariantName = "West" }
            ]
        };
        var reimported = new SqlServerTable
        {
            Name = "Orders",
            Indexes = [new Index { Name = "IX_Region", IndexColumns = "RegionId, Drifted" }]
        };

        // No variant active on this source — the extracted shape is kept ungated so SS-DUP-001 surfaces it.
        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => false);

        Assert.That(reimported.Indexes, Has.Count.EqualTo(3)); // both authored variants + the ungated extracted
        var ungated = reimported.Indexes.Single(i => string.IsNullOrEmpty(i.ShouldApplyExpression));
        Assert.That(ungated.IndexColumns, Is.EqualTo("RegionId, Drifted"));
        Assert.That(reimported.Indexes.Count(i => !string.IsNullOrEmpty(i.ShouldApplyExpression)), Is.EqualTo(2));
    }

    [Test]
    public void PreserveCustomProperties_GateEvaluationThrows_PropagatesFailClosed()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes =
            [
                new Index { Name = "IX_Region", IndexColumns = "RegionId", ShouldApplyExpression = "bad", VariantName = "A" },
                new Index { Name = "IX_Region", IndexColumns = "RegionId, Zone", ShouldApplyExpression = "worse", VariantName = "B" }
            ]
        };
        var reimported = new SqlServerTable
        {
            Name = "Orders",
            Indexes = [new Index { Name = "IX_Region", IndexColumns = "RegionId" }]
        };

        Assert.That(() => ImportTableHelper.PreserveDataDeliveryAndCustomProperties(
                reimported, original, _ => throw new System.InvalidOperationException("gate blew up")),
            Throws.InvalidOperationException);
    }

    [Test]
    public void PreserveCustomProperties_GatedComponent_SurvivesReimportWhenGatedOutOnSource()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes = [new Index { Name = "IX_Modern", IndexColumns = "JsonCol", ShouldApplyExpression = "1=0", VariantName = "Modern engines" }]
        };
        var reimported = new SqlServerTable { Name = "Orders", Indexes = [] };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.Indexes, Has.Count.EqualTo(1));
        Assert.That(reimported.Indexes[0].VariantName, Is.EqualTo("Modern engines"));
    }

    [Test]
    public void PreserveCustomProperties_UngatedComponent_NotResurrectedWhenAbsentOnSource()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Indexes = [new Index { Name = "IX_Dropped", IndexColumns = "OldCol" }]
        };
        var reimported = new SqlServerTable { Name = "Orders", Indexes = [] };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.Indexes, Is.Empty);
    }

    [Test]
    public void PreserveCustomProperties_RenamedComponent_StillMatchesByOldNameBeforeGatedSurvival()
    {
        var original = new SqlServerTable
        {
            Name = "Orders",
            Columns = [new Column { Name = "NewName", OldName = "OldName", ShouldApplyExpression = "1=1" }]
        };
        var newCol = new Column { Name = "OldName" };
        var reimported = new SqlServerTable { Name = "Orders", Columns = { newCol } };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.Columns, Has.Count.EqualTo(1));
        Assert.That(newCol.ShouldApplyExpression, Is.EqualTo("1=1"));
    }

    [Test]
    public void PreserveDataDelivery_MultiVariant_SurvivesReimportWholesale()
    {
        var original = new Table
        {
            Name = "[dbo].[Ref]",
            DataDelivery =
            [
                new Schema.Delivery.DataDelivery { ContentFile = "dev.json",  MergeType = "Insert", ShouldApplyExpression = "DB_NAME() = 'Dev'",  VariantName = "dev" },
                new Schema.Delivery.DataDelivery { ContentFile = "prod.json", MergeType = "Insert", ShouldApplyExpression = "DB_NAME() = 'Prod'", VariantName = "prod" }
            ]
        };
        var reimported = new Table
        {
            Name = "[dbo].[Ref]",
            DataDelivery = [new Schema.Delivery.DataDelivery { ContentFile = "stale.json", MergeType = "Insert" }]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.DataDelivery, Has.Count.EqualTo(2));
        Assert.That(reimported.DataDelivery[0].VariantName, Is.EqualTo("dev"));
        Assert.That(reimported.DataDelivery[1].VariantName, Is.EqualTo("prod"));
        Assert.That(reimported.DataDelivery, Has.None.Matches<Schema.Delivery.DataDelivery>(d => d.ContentFile == "stale.json"));
    }

    [Test]
    public void PreserveDataDelivery_SingleGated_SurvivesWhenAbsentOnSource()
    {
        var original = new Table
        {
            Name = "[dbo].[Ref]",
            DataDelivery = [new Schema.Delivery.DataDelivery { ContentFile = "seed.json", MergeType = "Insert", ShouldApplyExpression = "DB_NAME() = 'Dev'", VariantName = "dev-seed" }]
        };
        var reimported = new Table { Name = "[dbo].[Ref]", DataDelivery = [] };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.DataDelivery, Has.Count.EqualTo(1));
        Assert.That(reimported.DataDelivery[0].ShouldApplyExpression, Is.EqualTo("DB_NAME() = 'Dev'"));
    }

    [Test]
    public void PreserveDataDelivery_SingleUngated_PreservedFieldsFromOriginal()
    {
        // Ungated single delivery: today's behavior — original delivery config carries onto the reimport.
        var original = new Table
        {
            Name = "[dbo].[Ref]",
            DataDelivery = [new Schema.Delivery.DataDelivery { ContentFile = "data/Ref.tabledata", MergeType = "Insert/Update", MatchColumns = "Id" }]
        };
        var reimported = new Table { Name = "[dbo].[Ref]", DataDelivery = [] };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.DataDelivery, Has.Count.EqualTo(1));
        Assert.That(reimported.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
        Assert.That(reimported.DataDelivery[0].MatchColumns, Is.EqualTo("Id"));
    }

    [Test]
    public void PreserveDataDelivery_BothPresentSingle_OriginalReplacesExtracted()
    {
        var original = new Table
        {
            Name = "[dbo].[Ref]",
            DataDelivery = [new Schema.Delivery.DataDelivery { ContentFile = "authored.json", ShouldApplyExpression = "1=1", VariantName = "v" }]
        };
        var reimported = new Table
        {
            Name = "[dbo].[Ref]",
            DataDelivery = [new Schema.Delivery.DataDelivery { ContentFile = "extracted.json" }]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reimported, original, _ => true);

        Assert.That(reimported.DataDelivery, Has.Count.EqualTo(1));
        Assert.That(reimported.DataDelivery[0].ContentFile, Is.EqualTo("authored.json"));
        Assert.That(reimported.DataDelivery[0].VariantName, Is.EqualTo("v"));
    }

    // Re-extracting an unchanged database must rewrite nothing. The first re-extraction of every package added
    // "ShouldApplyExpression": "", "VariantName": "" and (MySQL) "OldName": "" throughout: carrying authored
    // properties forward coalesced a key the file never had to an empty string, and an empty string serializes.
    // The fresh JSON below is shaped like each engine's generator output -- PostgreSQL's already carries the empty
    // ShouldApplyExpression/OldName, MySQL's carries none -- so both ways of arriving at "unset" are covered.
    [TestCase(Platform.MySQL, """{ "Name": "customer", "Columns": [ { "Name": "customer_id", "DataType": "int" } ], "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`customer_id`" } ] }""")]
    [TestCase(Platform.PostgreSQL, """{ "Name": "device", "Columns": [ { "Name": "device_id", "DataType": "int4", "ShouldApplyExpression": "", "OldName": "" } ], "Indexes": [ { "Name": "device_pkey", "PrimaryKey": true, "Unique": true, "IndexColumns": "device_id", "ShouldApplyExpression": "" } ], "ShouldApplyExpression": "", "OldName": "" }""")]
    [TestCase(Platform.SqlServer, """{ "Schema": "[dbo]", "Name": "[Customer]", "Columns": [ { "Name": "[Id]", "DataType": "INT" }, { "Name": "[Doubled]", "DataType": "INT", "ComputedExpression": "[Id]*(2)", "Persisted": true, "Nullable": false } ], "Indexes": [ { "Name": "[PK_Customer]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }""")]
    [TestCase(Platform.MariaDb, """{ "Name": "customer", "Columns": [ { "Name": "customer_id", "DataType": "int(11)" } ], "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`customer_id`" } ] }""")]
    public void ReExtractingAnUnchangedTable_WritesTheSameFile(Platform platform, string extractedJson)
    {
        var onDisk = JsonHelper.Serialize(PlatformDeserializer.DeserializeTable(extractedJson, platform));

        var reExtracted = PlatformDeserializer.DeserializeTable(extractedJson, platform);
        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(reExtracted, PlatformDeserializer.DeserializeTable(onDisk, platform), _ => true);

        Assert.That(JsonHelper.Serialize(reExtracted), Is.EqualTo(onDisk));
    }
}
