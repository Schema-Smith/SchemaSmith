// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using Schema.Domain;
using Schema.Isolators;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class WhatIfParityTests : WhatIfParityTestsSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override IConfigurationRoot FixtureConfig { get; } = FactoryContainer.Resolve<IConfigurationRoot>();
}
