// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>MariaDB leg of <see cref="DatabaseCollationMismatchSharedTests"/>.</summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable] // creates and drops its own database
public class DatabaseCollationMismatchTests : DatabaseCollationMismatchSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
