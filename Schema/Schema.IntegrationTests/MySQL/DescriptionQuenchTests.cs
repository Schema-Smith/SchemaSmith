// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// MySQL binding of the shared table/column/index Description round-trip and drift tests.
/// </summary>
[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class DescriptionQuenchTests : DescriptionQuenchSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
