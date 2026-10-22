// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.MariaDb;

[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class ScriptObjectFidelityTests : ScriptObjectFidelitySharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";
    protected override string FixtureConnectionString => FixtureSetup.ConnectionString;
}
