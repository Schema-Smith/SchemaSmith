// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>MariaDb binding of the case-only name-difference tests.</summary>
[Category("MariaDb")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_CaseOnlyNameDifferenceTests : TableQuench_CaseOnlyNameDifferenceSharedTests
{
    protected override Platform Platform => Schema.Domain.Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;
}
