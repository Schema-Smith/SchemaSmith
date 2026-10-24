// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>MariaDb binding of the shared generated-column kind test (MY-022).</summary>
[Category("MariaDb")]
[TestFixture]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_GeneratedColumnKindTests : TableQuench_GeneratedColumnKindSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;
}
