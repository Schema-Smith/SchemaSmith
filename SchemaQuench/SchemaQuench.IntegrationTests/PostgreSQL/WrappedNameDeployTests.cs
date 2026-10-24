// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A table or column name written with PostgreSQL's double quotes deploys as the name inside them, and a name without
/// them is left exactly as written. Only the load-time notice was tested, not what reaches the database.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class WrappedNameDeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "WrappedNameProduct";

    private void CleanUp() =>
        Exec("DROP TABLE IF EXISTS public.\"WrappedUsers\", public.\"\"\"WrappedUsers\"\"\", public.\"PlainUsers\"; DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'WrappedNameProduct'");

    [Test]
    public void AWrappedName_DeploysUnwrapped_AndAPlainOneAsWritten()
    {
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "public.WrappedUsers.json", """
                { "Schema": "public", "Name": "\"WrappedUsers\"", "Columns": [ { "Name": "\"Email\"", "DataType": "text", "Nullable": true } ] }
                """);
            WriteFile("Tables", "public.PlainUsers.json", """
                { "Schema": "public", "Name": "PlainUsers", "Columns": [ { "Name": "Email", "DataType": "text", "Nullable": true } ] }
                """);

            Deploy();

            Assert.Multiple(() =>
            {
                Assert.That(Count("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'WrappedUsers' AND column_name = 'Email'"),
                    Is.EqualTo(1), "the wrapped table and column are created under the names inside the quotes");
                Assert.That(Count("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE '%WrappedUsers%' AND table_name LIKE '%\"%'"),
                    Is.Zero, "no name keeps its quotes");
                Assert.That(Count("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'PlainUsers' AND column_name = 'Email'"),
                    Is.EqualTo(1), "a name without quotes is taken as written, case and all");
            });
        }
        finally
        {
            CleanUp();
        }
    }
}
