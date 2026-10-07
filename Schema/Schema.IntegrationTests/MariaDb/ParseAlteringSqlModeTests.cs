// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;

namespace Schema.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable] // creates and drops its own database: a from-scratch kindle is part of what is under test
public class ParseAlteringSqlModeTests : ParseAlteringSqlModeSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override IEnumerable<string> ParseAlteringModes =>
        new[] { "ANSI_QUOTES,PIPES_AS_CONCAT,NO_BACKSLASH_ESCAPES,STRICT_TRANS_TABLES", "ORACLE" };
}
