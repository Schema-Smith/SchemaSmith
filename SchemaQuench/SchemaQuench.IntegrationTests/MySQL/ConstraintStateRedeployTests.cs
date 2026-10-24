// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

/// <summary>
/// A package that does not mention a check's enforcement leaves it as it is. Through SchemaQuench every check arrived
/// "Enforced": true, so a redeploy enforced a check someone had set NOT ENFORCED. MariaDB has no such state.
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class ConstraintStateRedeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";
    protected override string ProductName => "ConstraintStateProduct";

    private const string TableJson = """
        { "Name": "cs_state_orders",
          "Columns": [ { "Name": "id", "DataType": "INT", "Nullable": false } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ],
          "CheckConstraints": [ { "Name": "ck_cs_state_orders_id", "Expression": "id > 0" } ] }
        """;

    private void CleanUp() =>
        Exec("DROP TABLE IF EXISTS cs_state_orders; DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = 'ConstraintStateProduct'");

    [Test]
    public void ANotEnforcedCheck_StaysNotEnforced_WhenThePackageDoesNotMentionIt()
    {
        if (Count("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'information_schema' AND TABLE_NAME = 'TABLE_CONSTRAINTS' AND COLUMN_NAME = 'ENFORCED'") == 0)
            Assert.Ignore("A check's enforcement state needs MySQL 8.0.16.");
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "cs_state_orders.json", TableJson);
            Deploy();
            Exec("ALTER TABLE cs_state_orders ALTER CHECK ck_cs_state_orders_id NOT ENFORCED");

            Deploy();

            Assert.That(Scalar("SELECT ENFORCED FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'ck_cs_state_orders_id'")?.ToString(),
                Is.EqualTo("NO"), "the check stays not enforced");
        }
        finally
        {
            CleanUp();
        }
    }
}
