// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
public class WhatIfParityTests : WhatIfParityTestsSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override IConfigurationRoot FixtureConfig => FixtureSetup.Config;
}
