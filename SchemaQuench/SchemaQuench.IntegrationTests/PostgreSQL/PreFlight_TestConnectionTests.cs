// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[TestFixture]
[Category("PostgreSQL")]
[NonParallelizable]
public class PreFlight_TestConnectionTests : PreFlight_TestConnectionSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainConnectionString => Schema.IntegrationTests.PostgreSQL.FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => Schema.IntegrationTests.PostgreSQL.FixtureSetup.MainDb;
    protected override string ProductPlatformFolder => "PostgreSQL";
}
