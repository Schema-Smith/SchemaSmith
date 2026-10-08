// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using Schema.Domain;
using Schema.Isolators;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class WhatIfParityTests : WhatIfParityTestsSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override IConfigurationRoot FixtureConfig { get; } = FactoryContainer.Resolve<IConfigurationRoot>();
}
