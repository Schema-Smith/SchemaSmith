// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[TestFixture]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CustomTableHookTests : TableQuench_CustomTableHookTestsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string BaseConnectionString => FixtureSetup.ConnectionString;
}
