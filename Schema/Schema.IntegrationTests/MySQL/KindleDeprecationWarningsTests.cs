// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MySQL;

[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class KindleDeprecationWarningsTests : KindleDeprecationWarningsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ServerConnectionString => FixtureSetup.ConnectionString;
}
