// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MySQL;

/// <summary>MySQL binding of the shared expression-map kindling tests (#242).</summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class ExpressionMapKindlingTests : ExpressionMapKindlingSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
