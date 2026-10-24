// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class OwnershipRefusalTests : OwnershipRefusalSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string Quote(string name) => $"[{name}]";
    protected override string TableSchema => "dbo";

    protected override void ClearOwnership(string first, string second) =>
        Exec($"IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName IN ('{first}', '{second}')");
}
