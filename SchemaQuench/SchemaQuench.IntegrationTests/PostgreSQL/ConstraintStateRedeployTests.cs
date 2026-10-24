// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A package that does not mention a constraint's state leaves it as it is. Through SchemaQuench every constraint
/// arrived "Validated": true, so a redeploy validated a key and a check someone had added NOT VALID.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class ConstraintStateRedeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "ConstraintStateProduct";

    private const string TableJson = """
        { "Schema": "public", "Name": "cs_state_orders",
          "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "parent", "DataType": "integer", "Nullable": true } ],
          "Indexes": [ { "Name": "cs_state_orders_pkey", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ],
          "ForeignKeys": [ { "Name": "fk_cs_state_orders_parent", "Columns": "parent", "RelatedTableSchema": "public", "RelatedTable": "cs_state_orders", "RelatedColumns": "id" } ],
          "CheckConstraints": [ { "Name": "ck_cs_state_orders_id", "Expression": "id > 0" } ] }
        """;

    private void CleanUp() =>
        Exec("DROP TABLE IF EXISTS public.cs_state_orders; DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'ConstraintStateProduct'");

    [Test]
    public void ANotValidKeyAndCheck_StayNotValid_WhenThePackageDoesNotMentionTheirState()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "public.cs_state_orders.json", TableJson);
            Deploy();
            Exec("ALTER TABLE public.cs_state_orders DROP CONSTRAINT fk_cs_state_orders_parent, DROP CONSTRAINT ck_cs_state_orders_id; " +
                 "ALTER TABLE public.cs_state_orders ADD CONSTRAINT fk_cs_state_orders_parent FOREIGN KEY (parent) REFERENCES public.cs_state_orders (id) NOT VALID, " +
                 "ADD CONSTRAINT ck_cs_state_orders_id CHECK (id > 0) NOT VALID");

            Deploy();

            Assert.That(Count("SELECT COUNT(*) FROM pg_constraint WHERE conname IN ('fk_cs_state_orders_parent', 'ck_cs_state_orders_id') AND NOT convalidated"),
                Is.EqualTo(2), "both stay NOT VALID");
        }
        finally
        {
            CleanUp();
        }
    }
}
