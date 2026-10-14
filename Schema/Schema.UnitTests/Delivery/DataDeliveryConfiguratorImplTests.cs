// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using NSubstitute;
using Schema.Delivery;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Delivery;

[TestFixture]
public class DataDeliveryConfiguratorImplTests
{
    // Explicitly-rooted base (not the drive-relative "C:" + "template" -> "C:template") so the
    // downstream Path.Join chain is unambiguous; the file system is mocked so the location is inert.
    private static readonly string TemplateRoot = Path.Join(Path.GetTempPath(), "ss_dd_configurator_template");
    private static readonly string TablesDir = Path.Join(TemplateRoot, "Tables");
    private static readonly string TableJsonPath = Path.Join(TablesDir, "dbo.TestTable.json");
    private static readonly string ContentFilePath = Path.Join(TemplateRoot, "Content", "dbo.TestTable.tabledata");

    private IFile _file;
    private IDirectory _directory;
    private List<string> _warnings;
    private List<string> _progress;

    [SetUp]
    public void SetUp()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            _file = Substitute.For<IFile>();
            _directory = Substitute.For<IDirectory>();
            FactoryContainer.Register(_file);
            FactoryContainer.Register(_directory);
        }

        _warnings = new List<string>();
        _progress = new List<string>();

        _directory.Exists(TablesDir).Returns(true);
        _file.Exists(TableJsonPath).Returns(true);
    }

    [TearDown]
    public void TearDown()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
        }
    }

    private DataDeliveryConfiguratorContext MakeContext() => new()
    {
        TemplateRootPath = TemplateRoot,
        Platform = "SqlServer",
        TableSchema = "dbo",
        TableName = "TestTable",
        ContentFilePath = ContentFilePath,
        DefaultMergeType = "Insert/Update",
        WarningLog = _warnings.Add,
        ProgressLog = _progress.Add
    };

    [Test]
    public void Configure_WhenDataDeliveryAbsent_CreatesSingleObject()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": []
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(MakeContext());

        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s =>
            s.ContainsIgnoringCase("\"DataDelivery\": {") &&
            s.ContainsIgnoringCase("\"MergeType\": \"Insert/Update\"")));
    }

    [Test]
    public void Configure_XmlContentEncoding_StampsIt()
    {
        _file.ReadAllText(TableJsonPath).Returns("""
            { "Schema": "dbo", "Name": "TestTable", "Columns": [] }
            """);
        var context = MakeContext();
        context.ContentEncoding = "Xml";

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s =>
            s.ContainsIgnoringCase("\"ContentEncoding\": \"Xml\"")));
    }

    [Test]
    public void Configure_ExtractionTimeZone_IsRecorded_AndAbsentRemovesIt()
    {
        _file.ReadAllText(TableJsonPath).Returns("""
            { "Name": "TestTable", "Columns": [], "DataDelivery": { "ContentFile": "x", "TimeZone": "+05:00" } }
            """);
        var context = MakeContext();
        context.TimeZone = "+00:00";
        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);
        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s => s.Contains("\"TimeZone\": \"+00:00\"")));

        _file.ClearReceivedCalls();
        context.TimeZone = null;
        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);
        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s => !s.Contains("TimeZone")));
    }

    [Test]
    public void Configure_JsonContentEncoding_DoesNotStampIt()
    {
        // JSON is the default encoding, so no redundant "ContentEncoding": "Json" is written.
        _file.ReadAllText(TableJsonPath).Returns("""
            { "Schema": "dbo", "Name": "TestTable", "Columns": [] }
            """);
        var context = MakeContext();
        context.ContentEncoding = "Json";

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s =>
            !s.ContainsIgnoringCase("ContentEncoding")));
    }

    [Test]
    public void Configure_WhenDataDeliveryIsObject_UpdatesInPlace()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": {
                "ContentFile": "Content/dbo.TestTable.tabledata",
                "MergeType": "Insert"
              }
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(MakeContext());

        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s =>
            s.ContainsIgnoringCase("\"MergeType\": \"Insert/Update\"") &&
            !s.ContainsIgnoringCase("\"DataDelivery\": [")));
    }

    [Test]
    public void Configure_WhenDataDeliveryIsArray_LeavesArrayIntactAndWarns()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.prod.tabledata",
                  "MergeType": "Insert/Update",
                  "ShouldApplyExpression": "Target.Environment == 'Prod'",
                  "VariantName": "Prod"
                },
                {
                  "ContentFile": "Content/dbo.TestTable.dev.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Environment == 'Dev'",
                  "VariantName": "Dev"
                }
              ]
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(MakeContext());

        _file.DidNotReceiveWithAnyArgs().WriteAllText(default, default);
        Assert.That(_warnings, Has.Some.Contains("array"));
    }

    [Test]
    public void Configure_ArrayWithMatchingVariant_ReconcilesThatElement_PreservesGateAndOtherVariants()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.eu.stale.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU'",
                  "VariantName": "EU"
                },
                {
                  "ContentFile": "Content/dbo.TestTable.us.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'US'",
                  "VariantName": "US"
                }
              ]
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        var context = MakeContext();
        context.VariantName = "EU";

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        _file.Received(1).WriteAllText(TableJsonPath, Arg.Is<string>(s =>
            s.ContainsIgnoringCase("\"ContentFile\": \"Content/dbo.TestTable.tabledata\"") &&
            s.ContainsIgnoringCase("\"MergeType\": \"Insert/Update\"") &&
            s.ContainsIgnoringCase("\"ShouldApplyExpression\": \"Target.Region == 'EU'\"") &&
            s.ContainsIgnoringCase("\"VariantName\": \"EU\"") &&
            s.ContainsIgnoringCase("\"ContentFile\": \"Content/dbo.TestTable.us.tabledata\"") &&
            s.ContainsIgnoringCase("\"ShouldApplyExpression\": \"Target.Region == 'US'\"") &&
            s.ContainsIgnoringCase("\"VariantName\": \"US\"")));
    }

    [Test]
    public void Configure_ArrayWithVariantNotFound_LeavesArrayUntouched_Warns()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.eu.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU'",
                  "VariantName": "EU"
                }
              ]
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        var context = MakeContext();
        context.VariantName = "APAC";

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        _file.DidNotReceiveWithAnyArgs().WriteAllText(default, default);
        Assert.That(_warnings, Has.Some.Matches<string>(w => w.ContainsIgnoringCase("APAC") && w.ContainsIgnoringCase("not found")));
    }

    [Test]
    public void Configure_ArrayWithNoVariantProvided_LeavesArrayUntouched_Warns()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.eu.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU'",
                  "VariantName": "EU"
                }
              ]
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        var context = MakeContext();
        context.VariantName = null;

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        _file.DidNotReceiveWithAnyArgs().WriteAllText(default, default);
        Assert.That(_warnings, Has.Some.Matches<string>(w => w.ContainsIgnoringCase("array") && w.ContainsIgnoringCase("left untouched")));
    }

    [Test]
    public void Configure_ArrayWithAmbiguousVariant_LeavesArrayUntouched_Warns()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.eu1.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU' && Target.Shard == 1",
                  "VariantName": "EU"
                },
                {
                  "ContentFile": "Content/dbo.TestTable.eu2.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU' && Target.Shard == 2",
                  "VariantName": "EU"
                }
              ]
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        var context = MakeContext();
        context.VariantName = "EU";

        DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        _file.DidNotReceiveWithAnyArgs().WriteAllText(default, default);
        Assert.That(_warnings, Has.Some.Matches<string>(w => w.ContainsIgnoringCase("ambiguous")));
    }

    // Delivery-precedence (#390 part 2) keys merge-script suppression off this return value, per
    // table -- so it must be true whenever delivery was actually configured (changed or already up
    // to date) and false whenever Configure declined.
    [Test]
    public void Configure_TableJsonFound_ReturnsTrue()
    {
        _file.ReadAllText(TableJsonPath).Returns("""
            { "Schema": "dbo", "Name": "TestTable", "Columns": [] }
            """);

        var result = DataDeliveryConfiguratorImpl.GetFromFactory().Configure(MakeContext());

        Assert.That(result, Is.True);
    }

    [Test]
    public void Configure_AlreadyUpToDate_StillReturnsTrue()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": {
                "ContentFile": "Content/dbo.TestTable.tabledata",
                "MergeType": "Insert/Update"
              }
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        var result = DataDeliveryConfiguratorImpl.GetFromFactory().Configure(MakeContext());

        _file.DidNotReceiveWithAnyArgs().WriteAllText(default, default);
        Assert.That(result, Is.True, "Already-up-to-date is still 'delivery configured for this table'.");
    }

    [Test]
    public void Configure_TableJsonNotFound_ReturnsFalse()
    {
        _file.Exists(TableJsonPath).Returns(false);

        var result = DataDeliveryConfiguratorImpl.GetFromFactory().Configure(MakeContext());

        Assert.That(result, Is.False, "A table delivery never covered must not be reported as configured.");
    }

    [Test]
    public void Configure_ArrayWithNoVariantProvided_ReturnsFalse()
    {
        var tableJson = """
            {
              "Schema": "dbo",
              "Name": "TestTable",
              "Columns": [],
              "DataDelivery": [
                {
                  "ContentFile": "Content/dbo.TestTable.eu.tabledata",
                  "MergeType": "Insert",
                  "ShouldApplyExpression": "Target.Region == 'EU'",
                  "VariantName": "EU"
                }
              ]
            }
            """;
        _file.ReadAllText(TableJsonPath).Returns(tableJson);

        var context = MakeContext();
        context.VariantName = null;

        var result = DataDeliveryConfiguratorImpl.GetFromFactory().Configure(context);

        Assert.That(result, Is.False);
    }
}
