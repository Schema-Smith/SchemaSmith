// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
public class BacktickWrappingAgreesWithIdentifierUnwrapTests : BacktickWrappingAgreesWithIdentifierUnwrapSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string KindledDbConnectionString => FixtureSetup.GetMainDbConnectionString();
}
