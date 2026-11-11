// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using DataTongs.IntegrationTests.Shared;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;

namespace DataTongs.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class DataTongsCastTests : DataTongsCastSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";
    protected override string Database => FixtureSetup.MainDb;
    protected override string DatabaseConnectionString => FixtureSetup.GetMainDbConnectionString();
}
