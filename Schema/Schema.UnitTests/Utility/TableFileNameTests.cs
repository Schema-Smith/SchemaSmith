// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class TableFileNameTests
{
    [Test]
    public void Canonical_NoVariant_IsSchemaDotTableDotJson()
    {
        Assert.That(TableFileName.Canonical("dbo", "Orders", "", false), Is.EqualTo("dbo.Orders.json"));
    }

    [Test]
    public void Canonical_WithVariant_AppendsVariantAfterSchemaAndTable()
    {
        Assert.That(TableFileName.Canonical("dbo", "Orders", "EU", false), Is.EqualTo("dbo.Orders.EU.json"));
    }

    [Test]
    public void Canonical_SchemaLess_OmitsSchemaSegment()
    {
        Assert.That(TableFileName.Canonical("dbo", "Orders", "EU", true), Is.EqualTo("Orders.EU.json"));
    }

    [Test]
    public void Canonical_InASchemaTemplate_TheVariantFollowsATilde()
    {
        Assert.That(TableFileName.Canonical("", "Orders", "EU", schemaLess: true, inSchemaTemplate: true), Is.EqualTo("Orders~EU.json"));
        Assert.That(TableFileName.Canonical("", "Orders", "", schemaLess: true, inSchemaTemplate: true), Is.EqualTo("Orders.json"));
    }

    [Test]
    public void Canonical_SchemaTemplate_NoVariant_IsTableDotJson()
    {
        Assert.That(TableFileName.Canonical("dbo", "Orders", "", true), Is.EqualTo("Orders.json"));
    }

    [Test]
    public void Canonical_BlankOrWhitespaceVariant_TreatedAsNoVariant()
    {
        Assert.That(TableFileName.Canonical("dbo", "Orders", "   ", false), Is.EqualTo("dbo.Orders.json"));
    }

    [Test]
    public void Canonical_TrimsVariantBeforeEncoding()
    {
        Assert.That(TableFileName.Canonical("dbo", "Orders", "  EU  ", false), Is.EqualTo("dbo.Orders.EU.json"));
    }

    [Test]
    public void Canonical_StripsIdentifierQuoting_SqlServerBrackets()
    {
        // Table Name/Schema load quoted from JSON; the filename is the bare identifier.
        Assert.That(TableFileName.Canonical("[dbo]", "[Widget]", "", false), Is.EqualTo("dbo.Widget.json"));
    }

    [Test]
    public void Canonical_StripsIdentifierQuoting_MySqlBackticksNameOnly()
    {
        Assert.That(TableFileName.Canonical("", "`Widget`", "", true), Is.EqualTo("Widget.json"));
    }

    [Test]
    public void Canonical_StripsIdentifierQuoting_PostgresDoubleQuotes()
    {
        Assert.That(TableFileName.Canonical("\"public\"", "\"Orders\"", "\"EU\"", false), Is.EqualTo("public.Orders.EU.json"));
    }

    [Test]
    public void Canonical_EncodesEachSegmentWithFileNameEncoder()
    {
        var expected = $"{FileNameEncoder.Encode("dbo")}.{FileNameEncoder.Encode("Or/ders")}.{FileNameEncoder.Encode("E<U")}.json";
        Assert.That(TableFileName.Canonical("dbo", "Or/ders", "E<U", false), Is.EqualTo(expected));
    }
}
