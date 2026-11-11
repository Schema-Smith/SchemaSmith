// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class ZipPackageDeployTests : ZipPackageDeploySharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string TestProductsFolder => "MariaDb";
    protected override string MainConnectionString => Schema.IntegrationTests.MariaDb.FixtureSetup.GetMainDbConnectionString();
    // The rows the zipped TestTable.tabledata carries.
    protected override string DeliveredRowsSql => "SELECT COUNT(*) FROM TestTable WHERE TestID IN (1, 2) AND SomeText IN ('Root item', 'Child of root')";
    protected override int ExpectedDeliveredRows => 2;
}
