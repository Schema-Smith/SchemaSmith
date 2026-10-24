// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class OwnershipRefusalTests : OwnershipRefusalSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";
    protected override string Quote(string name) => $"`{name}`";
    protected override string TableSchema => MainDb;

    protected override void ClearOwnership(string first, string second) =>
        Exec($"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName IN ('{first}', '{second}')");
}
