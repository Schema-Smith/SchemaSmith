// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
public class ExpressionDefaultIdempotencyDeployTests : ExpressionDefaultIdempotencyDeployTestsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string BaseConnectionString => FixtureSetup.ConnectionString;
    protected override IConfigurationRoot FixtureConfig => FixtureSetup.Config;
}
