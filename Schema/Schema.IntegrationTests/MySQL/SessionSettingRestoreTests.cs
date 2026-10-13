// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class SessionSettingRestoreTests : SessionSettingRestoreSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
