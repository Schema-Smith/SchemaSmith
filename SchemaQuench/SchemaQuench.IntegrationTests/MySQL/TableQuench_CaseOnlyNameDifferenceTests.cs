// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

/// <summary>MySQL binding of the case-only name-difference tests.</summary>
[Category("MySQL")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_CaseOnlyNameDifferenceTests : TableQuench_CaseOnlyNameDifferenceSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;
}
