// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>SqlServer binding of the shared expression-map kindling tests (#242).</summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ExpressionMapKindlingTests : ExpressionMapKindlingSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
