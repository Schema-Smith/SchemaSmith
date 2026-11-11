// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class ZipPackageDeployTests : ZipPackageDeploySharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string TestProductsFolder => "MySQL";
    protected override string MainConnectionString => Schema.IntegrationTests.MySQL.FixtureSetup.GetMainDbConnectionString();
    // The rows the zipped TestTable.tabledata carries.
    protected override string DeliveredRowsSql => "SELECT COUNT(*) FROM TestTable WHERE TestID IN (1, 2) AND SomeText IN ('Root item', 'Child of root')";
    protected override int ExpectedDeliveredRows => 2;

    protected override bool TargetDeliversData
    {
        get
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(MainConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            return TargetVersionDetector.Detect(cmd, Platform.MySQL, conn.Database).ServerComparable is not (> 0 and < 800);
        }
    }
}
