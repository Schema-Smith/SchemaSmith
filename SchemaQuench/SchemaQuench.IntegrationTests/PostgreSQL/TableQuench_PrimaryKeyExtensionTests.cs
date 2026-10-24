// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Verifies TableQuench's ability to EXTEND an existing primary key with additional columns
/// (e.g. PK(a, b, c) -> PK(a, b, c, d, e)). The slice-2 CompletedMigrationScripts migration
/// relies on this capability — and at the time these tests were added, no existing test exercised
/// it. Lives at the TableQuench level rather than the kindling level so all consumers of
/// TableQuench (not just our specific kindling SQL) are covered.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PrimaryKeyExtensionTests : BaseTableQuenchTests
{
    [Test]
    public void ShouldExtendPrimaryKeyFromOneColumnToTwo()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT STRING_AGG(a.attname, ',' ORDER BY u.ord)
              FROM pg_index idx
              JOIN pg_class i ON i.oid = idx.indexrelid
              JOIN pg_class t ON t.oid = idx.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              JOIN pg_attribute a ON a.attrelid = idx.indrelid
              CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, ord)
              WHERE n.nspname = 'public'
                AND t.relname = 'extendpkonetotwo'
                AND idx.indisprimary
                AND a.attnum = u.element;";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Column1,Column2"));

        conn.Close();
    }

    [Test]
    public void ShouldExtendPrimaryKeyFromThreeColumnsToFive_MirrorsCompletedMigrationScriptsScenario()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT STRING_AGG(a.attname, ',' ORDER BY u.ord)
              FROM pg_index idx
              JOIN pg_class i ON i.oid = idx.indexrelid
              JOIN pg_class t ON t.oid = idx.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              JOIN pg_attribute a ON a.attrelid = idx.indrelid
              CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, ord)
              WHERE n.nspname = 'public'
                AND t.relname = 'extendpkthreetofive'
                AND idx.indisprimary
                AND a.attnum = u.element;";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PathCol,ProductCol,SlotCol,TemplateCol,SchemaCol"));
    }

    [Test]
    public void ExtendedPrimaryKey_AcceptsRowsThatWouldHaveCollidedOnLegacyPk()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM public.extendpkthreetofive";
        Assert.That(System.Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(2),
            "OneTimeSetUp must have inserted two rows with identical first-three-cols but different new-cols.");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE public.extendpkonetotwo (
    ""Column1"" INT NOT NULL,
    CONSTRAINT ""PK_extendpkonetotwo"" PRIMARY KEY (""Column1"")
);

CREATE TABLE public.extendpkthreetofive (
    ""PathCol"" VARCHAR(800) NOT NULL,
    ""ProductCol"" VARCHAR(100) NOT NULL,
    ""SlotCol"" VARCHAR(30) NOT NULL,
    CONSTRAINT ""PK_extendpkthreetofive"" PRIMARY KEY (""PathCol"", ""ProductCol"", ""SlotCol"")
);
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "public",
                "Name": "extendpkonetotwo",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false },
                    { "Name": "Column2", "DataType": "INT", "Nullable": false, "Default": "0" }
                ],
                "Indexes": [
                    {
                        "Name": "PK_extendpkonetotwo",
                        "IndexColumns": "Column1,Column2",
                        "PrimaryKey": true,
                        "Unique": true,
                        "Clustered": true
                    }
                ]
            },
            {
                "Schema": "public",
                "Name": "extendpkthreetofive",
                "Columns": [
                    { "Name": "PathCol", "DataType": "VARCHAR(800)", "Nullable": false },
                    { "Name": "ProductCol", "DataType": "VARCHAR(100)", "Nullable": false },
                    { "Name": "SlotCol", "DataType": "VARCHAR(30)", "Nullable": false },
                    { "Name": "TemplateCol", "DataType": "VARCHAR(256)", "Nullable": false, "Default": "''''" },
                    { "Name": "SchemaCol", "DataType": "VARCHAR(256)", "Nullable": false, "Default": "''''" }
                ],
                "Indexes": [
                    {
                        "Name": "PK_extendpkthreetofive",
                        "IndexColumns": "PathCol,ProductCol,SlotCol,TemplateCol,SchemaCol",
                        "PrimaryKey": true,
                        "Unique": true,
                        "Clustered": true
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        // Behavioral check: inserts that would have collided on legacy 3-col PK but are
        // distinct under the extended 5-col PK. Fails OneTimeSetUp if TableQuench didn't
        // extend the PK.
        cmd.CommandText = @"
INSERT INTO public.extendpkthreetofive (""PathCol"", ""ProductCol"", ""SlotCol"", ""TemplateCol"", ""SchemaCol"")
VALUES ('Migration_001.sql', 'Demo', 'Before', 'TenantA', '');

INSERT INTO public.extendpkthreetofive (""PathCol"", ""ProductCol"", ""SlotCol"", ""TemplateCol"", ""SchemaCol"")
VALUES ('Migration_001.sql', 'Demo', 'Before', 'TenantB', '');";
        cmd.ExecuteNonQuery();
    }
}
