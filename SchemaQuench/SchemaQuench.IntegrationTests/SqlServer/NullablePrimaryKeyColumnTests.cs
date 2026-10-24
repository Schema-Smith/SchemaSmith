// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class NullablePrimaryKeyColumnTests : NullablePrimaryKeyColumnSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string Quote(string name) => $"[{name}]";
    protected override string PrimaryKeyName => "[PK_nullable_key_probe]";
    protected override string TableSchema => "dbo";
}
