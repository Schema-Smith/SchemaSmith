// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MySQL;

/// <summary>MySQL leg of <see cref="DatabaseCollationMismatchSharedTests"/>. The MySQL branch of the
/// encryption comparison carries the same degraded COALESCE shape as the MariaDB one, so it needs the
/// same guard (Rule 20 -- no accidental single-platform coverage).</summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable] // creates and drops its own database
public class DatabaseCollationMismatchTests : DatabaseCollationMismatchSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
}
