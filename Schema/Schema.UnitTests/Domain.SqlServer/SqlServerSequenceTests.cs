// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.UnitTests.Domain.SqlServer;

[TestFixture]
public class SqlServerSequenceTests
{
    // decimal(38,0) is the widest sequence type; its bounds are 38 digits, past long and .NET decimal.
    private static readonly BigInteger Max38 = BigInteger.Pow(10, 38) - 1;

    [Test]
    public void ThirtyEightDigitBounds_SurviveAPackageRoundTrip()
    {
        var json = $$"""{ "Name": "S", "DataType": "decimal(38,0)", "MinValue": -{{Max38}}, "MaxValue": {{Max38}}, "Start": -{{Max38}} }""";

        var sequence = JsonConvert.DeserializeObject<SqlServerSequence>(json)!;
        var back = JObject.Parse(JsonConvert.SerializeObject(sequence));

        Assert.Multiple(() =>
        {
            Assert.That(sequence.MaxValue, Is.EqualTo(Max38));
            Assert.That(back["MinValue"]!.ToString(), Is.EqualTo("-" + Max38));
            Assert.That(back["MinValue"]!.Type, Is.EqualTo(JTokenType.Integer), "written as a number, as a package author writes it");
        });
    }

    [Test]
    public void ThirtyEightDigitBounds_ReachTheDeployProcedureIntact()
    {
        var sequence = new SqlServerSequence { Name = "S", DataType = "decimal(38,0)", MaxValue = Max38 };

        var xml = ModelXmlSerializer.ToIngestXml(new JArray(JObject.FromObject(sequence)).ToString(), "Sequences", "Sequence");

        Assert.That(xml, Does.Contain($"<MaxValue>{Max38}</MaxValue>"));
    }

    [Test]
    public void TheEditorSchema_TypesTheBoundsAsIntegers()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SqlServerSequence));

        Assert.Multiple(() =>
        {
            foreach (var property in new[] { "Start", "MinValue", "MaxValue", "Increment", "Cache" })
                Assert.That(schema["properties"]?[property]?.ToString(), Does.Contain("integer"), property);
        });
    }

    [Test]
    public void AnOmittedDataTypeAndIncrement_AreNotWrittenBack()
    {
        var sequence = JsonConvert.DeserializeObject<SqlServerSequence>("""{ "Name": "S" }""")!;
        var back = JObject.Parse(JsonConvert.SerializeObject(sequence, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }));

        Assert.Multiple(() =>
        {
            Assert.That(sequence.DataType, Is.EqualTo("bigint"));
            Assert.That(back.ContainsKey("DataType"), Is.False);
            Assert.That(back.ContainsKey("Increment"), Is.False);
        });
    }
}
