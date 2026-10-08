// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
public class WhatIfParityTests : WhatIfParityTestsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override IConfigurationRoot FixtureConfig => FixtureSetup.Config;
}
