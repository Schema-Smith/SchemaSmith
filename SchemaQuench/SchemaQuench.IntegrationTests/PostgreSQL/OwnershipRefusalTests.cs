// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class OwnershipRefusalTests : OwnershipRefusalSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string Quote(string name) => name;
    protected override string TableSchema => "public";

    protected override void ClearOwnership(string first, string second) =>
        Exec($"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" IN ('{first}', '{second}')");
}
