// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDb binding of the shared bootstrap index-shape tests.
/// </summary>
[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class BootstrapIndexShapeTests : BootstrapIndexShapeSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
