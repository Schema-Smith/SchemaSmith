// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ZipPackageDeployTests : ZipPackageDeploySharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string TestProductsFolder => "SqlServer";
    protected override string MainConnectionString => Schema.IntegrationTests.SqlServer.FixtureSetup.GetMainDbConnectionString();
    // The rows the zipped TestTable.tabledata carries.
    protected override string DeliveredRowsSql => "SELECT COUNT(*) FROM dbo.TestTable WHERE TestID IN ('1F1290C7-8E05-4D93-9080-2EE836B297CA', 'D18A72C7-94B1-4E90-8C07-882AB1339721', '4B68BC1E-AFA6-41E2-B602-88829807B191')";
    protected override int ExpectedDeliveredRows => 3;
}
