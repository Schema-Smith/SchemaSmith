// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class SessionSettingRestoreTests : SessionSettingRestoreSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
