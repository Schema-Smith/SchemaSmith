// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// A string that looks like an ISO date must come back exactly as written. The default reader made it a DateTime in the
/// host's time zone, so it was written back in another form, different on each machine. The milliseconds make the
/// difference visible on every host, whatever its zone.
/// </summary>
[TestFixture]
public class JsonTextTests
{
    private const string DateLike = "2026-10-07T12:00:00.000+02:00";

    [Test]
    public void Parse_KeepsADateLikeStringAsWritten()
    {
        var obj = JsonText.ParseObject($"{{\"When\":\"{DateLike}\"}}");

        Assert.That(obj["When"]!.Type, Is.EqualTo(JTokenType.String));
        Assert.That(obj.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo($"{{\"When\":\"{DateLike}\"}}"));
    }

    [Test]
    public void ParseArray_KeepsADateLikeValueInARow()
    {
        var rows = JsonText.ParseArray($"[{{\"CreatedOn\":\"{DateLike}\"}}]");

        Assert.That(rows.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo($"[{{\"CreatedOn\":\"{DateLike}\"}}]"));
    }

    [Test]
    public void Parse_RejectsTrailingContent_AsJTokenParseDoes()
    {
        Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => JsonText.Parse("{} {}"));
    }

    [TestCase("[]")]
    [TestCase("1")]
    public void ParseObject_OfSomethingElse_IsAReaderError(string json)
    {
        Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => JsonText.ParseObject(json));
    }

    // Every object type that carries Extensions, read and written back the way SchemaTongs and the deploy do.
    [Test]
    public void Table_RoundTripsDateLikeExtensionsOnEveryObjectType_ByteExact()
    {
        var ext = $"{{\"ReviewedOn\":\"{DateLike}\"}}";
        var json = "{\"Name\":\"[Customer]\",\"Schema\":\"[dbo]\",\"Extensions\":" + ext
                   + ",\"Columns\":[{\"Name\":\"[Id]\",\"DataType\":\"INT\",\"Extensions\":" + ext + "}]"
                   + ",\"Indexes\":[{\"Name\":\"[PK_Customer]\",\"PrimaryKey\":true,\"IndexColumns\":\"[Id]\",\"Extensions\":" + ext + "}]"
                   + ",\"ForeignKeys\":[{\"Name\":\"[FK_Customer_Self]\",\"Columns\":\"[Id]\",\"RelatedTable\":\"[Customer]\",\"RelatedColumns\":\"[Id]\",\"Extensions\":" + ext + "}]"
                   + ",\"CheckConstraints\":[{\"Name\":\"[CK_Customer]\",\"Expression\":\"[Id]>0\",\"Extensions\":" + ext + "}]}";

        var written = JsonHelper.Serialize(PlatformDeserializer.DeserializeTable(json, Platform.SqlServer));

        Assert.That(System.Text.RegularExpressions.Regex.Matches(written, System.Text.RegularExpressions.Regex.Escape(DateLike)).Count,
            Is.EqualTo(5), written);
    }

    // The deploy payload: the table definition rendered to ingest XML.
    [Test]
    public void ToIngestXmlObject_KeepsADateLikeValue()
    {
        var xml = ModelXmlSerializer.ToIngestXmlObject($"{{\"Name\":\"T\",\"Extensions\":{{\"ReviewedOn\":\"{DateLike}\"}}}}", "Table");

        Assert.That(xml, Does.Contain(DateLike));
    }
}
