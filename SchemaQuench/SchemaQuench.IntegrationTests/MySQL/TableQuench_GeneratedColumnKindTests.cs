// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

/// <summary>MySQL binding of the shared generated-column kind test (MY-022).</summary>
[Category("MySQL")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_GeneratedColumnKindTests : TableQuench_GeneratedColumnKindSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;
}
