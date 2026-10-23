// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A package that does not mention a constraint's state leaves it as it is. Through SchemaQuench every constraint
/// arrived "Enforced": true, "Validated": true, so a redeploy re-enabled a key and a check someone had disabled.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ConstraintStateRedeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "ConstraintStateProduct";

    private const string TableJson = """
        { "Schema": "[dbo]", "Name": "[cs_state_orders]",
          "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false }, { "Name": "[parent]", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "[PK_cs_state_orders]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ],
          "ForeignKeys": [ { "Name": "[FK_cs_state_orders_parent]", "Columns": "[parent]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[cs_state_orders]", "RelatedColumns": "[id]" } ],
          "CheckConstraints": [ { "Name": "[CK_cs_state_orders_id]", "Expression": "[id] > 0" } ] }
        """;

    private void CleanUp() =>
        Exec("IF OBJECT_ID('dbo.cs_state_orders') IS NOT NULL DROP TABLE [dbo].[cs_state_orders]; " +
             "IF OBJECT_ID('SchemaSmith.ProductOwnership', 'U') IS NOT NULL DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'ConstraintStateProduct'");

    [Test]
    public void ADisabledKeyAndCheck_StayDisabled_WhenThePackageDoesNotMentionTheirState()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "cs_state_orders.json", TableJson);
            Deploy();
            Exec("ALTER TABLE [dbo].[cs_state_orders] NOCHECK CONSTRAINT [FK_cs_state_orders_parent], [CK_cs_state_orders_id]");

            Deploy();

            Assert.Multiple(() =>
            {
                Assert.That(Count("SELECT CAST(is_disabled AS INT) FROM sys.foreign_keys WHERE [name] = 'FK_cs_state_orders_parent'"), Is.EqualTo(1), "the key stays disabled");
                Assert.That(Count("SELECT CAST(is_disabled AS INT) FROM sys.check_constraints WHERE [name] = 'CK_cs_state_orders_id'"), Is.EqualTo(1), "the check stays disabled");
            });
        }
        finally
        {
            CleanUp();
        }
    }

    // A key that fails to add stops the step, as it does on PostgreSQL. The four procedures went as one batch, which a
    // severity-16 error does not end, so the constraint-state and extended-property passes ran and logged after it.
    [Test]
    public void AForeignKeyThatFailsToAdd_StopsTheStep()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "dbo.cs_state_orders.json", TableJson);
            Deploy();
            Exec("ALTER TABLE [dbo].[cs_state_orders] DROP CONSTRAINT [FK_cs_state_orders_parent]; " +
                 "INSERT [dbo].[cs_state_orders] ([id], [parent]) VALUES (1, 999)");

            var log = Deploy(expectSuccess: false).Log.Where(l => l != null).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(log, Has.Some.Contains("FK_cs_state_orders_parent").And.StartsWith("ERROR"), "the key's failure is reported");
                Assert.That(log, Has.None.Contains("Converge constraint enforcement and validation"), string.Join("\n", log));
            });
        }
        finally
        {
            CleanUp();
        }
    }
}
