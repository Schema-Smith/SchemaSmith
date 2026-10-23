// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A foreign key into a table leaving the product is dropped first, whatever its name. The line that announces it put the
/// name into a RAISE NOTICE literal as it was, so a % read as a placeholder and a quote ended the literal.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class InboundForeignKeyNameTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "InboundFkNameProduct";

    private void CleanUp() =>
        Exec("DROP TABLE IF EXISTS public.inbound_fk_child, public.inbound_fk_parent; DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'InboundFkNameProduct'");

    [Test]
    public void AKeyNamedWithAPercentAndAQuote_IntoARemovedTable_IsDropped()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "public.inbound_fk_parent.json", """
                { "Schema": "public", "Name": "inbound_fk_parent",
                  "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
                  "Indexes": [ { "Name": "inbound_fk_parent_pkey", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
                """);
            Deploy();
            Exec("CREATE TABLE public.inbound_fk_child (parent integer CONSTRAINT \"fk_50%_parent's\" REFERENCES public.inbound_fk_parent (id))");

            WritePackage("");
            Deploy(dropRemovedTables: true);

            Assert.Multiple(() =>
            {
                Assert.That(Count("SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = 'inbound_fk_parent'"), Is.Zero, "the removed table is dropped");
                Assert.That(Count("SELECT COUNT(*) FROM pg_constraint WHERE conname = 'fk_50%_parent''s'"), Is.Zero, "and the key into it first");
            });
        }
        finally
        {
            CleanUp();
        }
    }
}
