// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[TestFixture]
[Category("SqlServer")]
[NonParallelizable]
public class PreFlight_TestConnectionTests : PreFlight_TestConnectionSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainConnectionString => Schema.IntegrationTests.SqlServer.FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => Schema.IntegrationTests.SqlServer.FixtureSetup.MainDb;
    protected override string ProductPlatformFolder => "SqlServer";
}
