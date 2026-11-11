// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using DataTongs.IntegrationTests.Shared;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;

namespace DataTongs.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class DataTongsCastTests : DataTongsCastSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ConfigPrefix => "MySQL";
    protected override string Database => FixtureSetup.MainDb;
    protected override string DatabaseConnectionString => FixtureSetup.GetMainDbConnectionString();
}
