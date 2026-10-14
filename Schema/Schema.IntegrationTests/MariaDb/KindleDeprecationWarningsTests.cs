// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MariaDb;

[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class KindleDeprecationWarningsTests : KindleDeprecationWarningsSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ServerConnectionString => FixtureSetup.ConnectionString;
}
