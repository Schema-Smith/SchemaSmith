// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Text.RegularExpressions;
using Schema.Utility;

namespace Schema.UnitTests.Domain.PostgreSQL;

[TestFixture]
public class PostgreSqlSequenceTests
{
    // The deploy reads only logged and unlogged, so a misspelt value passed --Validate and was ignored at deploy.
    [TestCase("Unlogged", true)]
    [TestCase("logged", true)]
    [TestCase("Temporary", false)]
    public void PersistenceType_AcceptsOnlyWhatTheDeployReads(string value, bool accepted)
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.PostgreSQL.PostgreSqlSequence));
        var pattern = schema["properties"]?["PersistenceType"]?["pattern"]?.ToString();
        Assert.That(pattern, Is.Not.Null, "the property declares a pattern");
        Assert.That(Regex.IsMatch(value, pattern!), Is.EqualTo(accepted));
    }
}
