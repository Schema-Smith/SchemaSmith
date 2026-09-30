// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
public class ExpressionDefaultIdempotencyDeployTests : ExpressionDefaultIdempotencyDeployTestsSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string BaseConnectionString => FixtureSetup.ConnectionString;
    protected override IConfigurationRoot FixtureConfig => FixtureSetup.Config;
}
