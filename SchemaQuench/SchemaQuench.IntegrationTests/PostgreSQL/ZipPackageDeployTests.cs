// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class ZipPackageDeployTests : ZipPackageDeploySharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string TestProductsFolder => "PostgreSQL";
    protected override string MainConnectionString => Schema.IntegrationTests.PostgreSQL.FixtureSetup.GetMainDbConnectionString();
    // The rows the zipped TestTable.tabledata carries.
    protected override string DeliveredRowsSql => "SELECT COUNT(*) FROM public.\"TestTable\" WHERE \"TestID\"::text IN ('1f1290c7-8e05-4d93-9080-2ee836b297ca', 'd18a72c7-94b1-4e90-8c07-882ab1339721', '4b68bc1e-afa6-41e2-b602-88829807b191')";
    protected override int ExpectedDeliveredRows => 3;
}
