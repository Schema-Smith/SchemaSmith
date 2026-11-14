// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
public class SchemaShearsSubsetSafetyTests : SchemaShearsSubsetSafetyTestsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string TestConnectionString => FixtureSetup.ConnectionString + "Database=information_schema;";
    protected override IConfigurationRoot FixtureConfig => FixtureSetup.Config;
    protected override string ProductPlatformFolder => "MySQL";
}
