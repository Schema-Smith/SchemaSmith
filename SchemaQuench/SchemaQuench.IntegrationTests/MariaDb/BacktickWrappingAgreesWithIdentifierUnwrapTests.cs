// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
public class BacktickWrappingAgreesWithIdentifierUnwrapTests : BacktickWrappingAgreesWithIdentifierUnwrapSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string KindledDbConnectionString => FixtureSetup.GetMainDbConnectionString();
}
