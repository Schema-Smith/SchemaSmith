// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// PostgreSQL allows a double quote in a name; SchemaSmith wrapped stored names in double quotes without escaping, so such
/// a name produced invalid DDL everywhere (a bare a"b became "a"b"). A table, its columns, indexes, keys, constraints and
/// statistics named that way now deploy, redeploy as unchanged, take a change, and extract under their own names.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class QuotedIdentifierTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "QuotedIdentifiers";

    private const string Drops = "DROP TABLE IF EXISTS \"q\"\"s\".\"Or\"\"ders\"; DROP TABLE IF EXISTS \"q\"\"s\".\"Pa\"\"rent\"; DROP SCHEMA IF EXISTS \"q\"\"s\" CASCADE";

    private void Write(string labelType)
    {
        WritePackage("""{ "FolderPath": "Schemas", "QuenchSlot": "Objects", "ObjectType": "Schemas" }""", "Schemas");
        WriteFile("Schemas", "qs.sql", "CREATE SCHEMA IF NOT EXISTS \"q\"\"s\";");
        WriteFile("Tables", "qs.parent.json", """
            { "Schema": "q\"s", "Name": "Pa\"rent",
              "Columns": [ { "Name": "I\"d", "DataType": "integer", "Nullable": false } ],
              "Indexes": [ { "Name": "pk\"parent", "PrimaryKey": true, "Unique": true, "IndexColumns": "\"I\"\"d\"" } ] }
            """);
        WriteFile("Tables", "qs.orders.json", $$"""
            { "Schema": "q\"s", "Name": "Or\"ders",
              "Columns": [ { "Name": "I\"d", "DataType": "integer", "Nullable": false },
                           { "Name": "Pa\"rentId", "DataType": "integer", "Nullable": true },
                           { "Name": "Q\"ty", "DataType": "integer", "Nullable": false, "Default": "0" },
                           { "Name": "La\"bel", "DataType": "{{labelType}}", "Nullable": true, "Description": "What \"it\" is" } ],
              "Indexes": [ { "Name": "pk\"orders", "PrimaryKey": true, "Unique": true, "IndexColumns": "\"I\"\"d\"" },
                           { "Name": "ix\"label", "IndexColumns": "\"La\"\"bel\"", "FilterExpression": "\"Q\"\"ty\" > 0" } ],
              "ForeignKeys": [ { "Name": "fk\"parent", "Columns": "\"Pa\"\"rentId\"", "RelatedTableSchema": "q\"s", "RelatedTable": "Pa\"rent", "RelatedColumns": "\"I\"\"d\"" } ],
              "CheckConstraints": [ { "Name": "ck\"qty", "Expression": "\"Q\"\"ty\" >= 0" } ],
              "Statistics": [ { "Name": "st\"label", "StatisticsColumns": "\"La\"\"bel\", \"Q\"\"ty\"" } ] }
            """);
    }

    // The other names that reach DDL: a table and a column renamed by OldName, an exclude constraint, the replica identity
    // index, an enum type, a sequence, and a materialized view with an index of its own.
    [Test]
    public void RenamesAndModeledObjectsWhoseNamesHoldADoubleQuote_Deploy_AndRedeployUnchanged()
    {
        const string drops = "DROP MATERIALIZED VIEW IF EXISTS \"q\"\"s\".\"Mv\"\"x\"; DROP TABLE IF EXISTS \"q\"\"s\".\"Re\"\"named\"; DROP TABLE IF EXISTS \"q\"\"s\".\"Ol\"\"d\"; " +
                             "DROP TYPE IF EXISTS \"q\"\"s\".\"Mo\"\"od\"; DROP DOMAIN IF EXISTS \"q\"\"s\".\"Qu\"\"antity\"; DROP SEQUENCE IF EXISTS \"q\"\"s\".\"Se\"\"q\"; DROP SCHEMA IF EXISTS \"q\"\"s\" CASCADE";
        Exec(drops);
        try
        {
            // J escapes a name for JSON, Q quotes it for SQL; the names themselves are written plainly.
            static string J(string name) => name.Replace("\"", "\\\"");
            static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
            void WriteAll(string table, string oldTable, string column, string oldColumn)
            {
                WritePackage("""{ "FolderPath": "Schemas", "QuenchSlot": "Objects", "ObjectType": "Schemas" }""", "Schemas", "Enum Types", "Sequences", "Materialized Views", "Domain Types");
                WriteFile("Schemas", "qs.sql", "CREATE SCHEMA IF NOT EXISTS \"q\"\"s\";");
                WriteFile("Enum Types", "qs.mood.json", """{ "Schema": "q\"s", "Name": "Mo\"od", "Values": [ "sa\"d", "ok" ] }""");
                WriteFile("Domain Types", "qs.qty.json", """{ "Schema": "q\"s", "Name": "Qu\"antity", "DataType": "integer", "CheckConstraints": [ { "Name": "qu\"positive", "Expression": "VALUE >= 0" } ] }""");
                WriteFile("Sequences", "qs.seq.json", """{ "Schema": "q\"s", "Name": "Se\"q", "Start": 1, "Increment": 1 }""");
                var oldTableJson = oldTable == null ? "" : $", \"OldName\": \"{J(oldTable)}\"";
                var oldColumnJson = oldColumn == null ? "" : $", \"OldName\": \"{J(oldColumn)}\"";
                WriteFile("Tables", "qs.renamed.json", $$"""
                    { "Schema": "q\"s", "Name": "{{J(table)}}"{{oldTableJson}}, "ReplicaIdentity": "INDEX", "ReplicaIdentityIndex": "ux\"code",
                      "Columns": [ { "Name": "I\"d", "DataType": "integer", "Nullable": false },
                                   { "Name": "{{J(column)}}", "DataType": "integer", "Nullable": false{{oldColumnJson}} } ],
                      "Indexes": [ { "Name": "pk\"ren", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(Q("I\"d"))}}" },
                                   { "Name": "ux\"code", "Unique": true, "IndexColumns": "{{J(Q(column))}}" } ],
                      "ExcludeConstraints": [ { "Name": "ex\"code", "AccessMethod": "btree",
                                                "ExcludeColumns": [ { "Column": "{{J(column)}}", "Operator": "=" } ] } ] }
                    """);
                WriteFile("Materialized Views", "qs.mv.json", $$"""
                    { "Schema": "q\"s", "Name": "Mv\"x", "Definition": "{{J("SELECT count(*) AS n FROM " + Q("q\"s") + "." + Q(table))}}",
                      "Indexes": [ { "Name": "mvix\"n", "IndexColumns": "n" } ] }
                    """);
            }
            WriteAll("Ol\"d", null, "Co\"de", null);
            Deploy();
            WriteAll("Re\"named", "Ol\"d", "Co\"de2", "Co\"de");
            Deploy();
            Assert.Multiple(() =>
            {
                Assert.That(Count("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"q\"\"s\".\"Re\"\"named\"'::regclass AND attname = 'Co\"de2'"), Is.EqualTo(1), "the table and column were renamed");
                Assert.That(Scalar("SELECT relreplident::text FROM pg_class WHERE oid = '\"q\"\"s\".\"Re\"\"named\"'::regclass"), Is.EqualTo("i"), "the replica identity is the index");
                Assert.That(Count("SELECT count(*) FROM pg_constraint WHERE conname = 'ex\"code' AND contype = 'x'"), Is.EqualTo(1), "the exclude constraint");
                Assert.That(Count("SELECT count(*) FROM pg_type WHERE typname = 'Mo\"od'"), Is.EqualTo(1), "the enum type");
                Assert.That(Count("SELECT count(*) FROM pg_constraint c JOIN pg_type t ON t.oid = c.contypid WHERE t.typname = 'Qu\"antity' AND c.conname = 'qu\"positive'"), Is.EqualTo(1), "the domain and its check");
                Assert.That(Count("SELECT count(*) FROM pg_class WHERE relname = 'Se\"q' AND relkind = 'S'"), Is.EqualTo(1), "the sequence");
                Assert.That(Count("SELECT count(*) FROM pg_class WHERE relname = 'mvix\"n' AND relkind = 'i'"), Is.EqualTo(1), "the materialized view's index");
            });
            var again = Deploy();
            Assert.That(again.Audit.Where(r => r.Action is "created" or "modified" or "dropped"), Is.Empty, "a redeploy changes nothing: " + again.DescribeAudit());
        }
        finally
        {
            Exec(drops);
        }
    }

    [Test]
    public void ATableWhoseNamesHoldADoubleQuote_Deploys_Redeploys_Changes_AndExtracts()
    {
        Exec(Drops);
        try
        {
            Write("varchar(40)");
            Deploy();
            Assert.That(Count("SELECT count(*) FROM pg_constraint WHERE conname IN ('fk\"parent', 'ck\"qty', 'pk\"orders')"), Is.EqualTo(3), "the keys and the check exist");
            Assert.That(Count("SELECT count(*) FROM pg_class WHERE relname IN ('ix\"label') AND relkind = 'i'"), Is.EqualTo(1), "and the index");
            Assert.That(Count("SELECT count(*) FROM pg_statistic_ext WHERE stxname = 'st\"label'"), Is.EqualTo(1), "and the statistics");

            var again = Deploy();
            Assert.That(again.Audit.Where(r => r.Action is "created" or "modified" or "dropped"), Is.Empty, "a redeploy changes nothing: " + again.DescribeAudit());

            Write("varchar(80)");
            Deploy();
            Assert.That(Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = '\"q\"\"s\".\"Or\"\"ders\"'::regclass AND attname = 'La\"bel'"),
                Is.EqualTo("character varying(80)"), "the change applied");

            var extracted = Scalar("SELECT \"SchemaSmith\".\"GenerateTableJSON\"('q\"s', 'Or\"ders')")?.ToString() ?? "";
            Assert.That(extracted, Does.Contain("fk\\\"parent").And.Contain("ix\\\"label").And.Contain("ck\\\"qty").And.Contain("st\\\"label").And.Contain("La\\\"bel"),
                "extraction names each object as it is");

            // And what extraction wrote deploys as it stands: a package built from it changes nothing.
            WriteFile("Tables", "qs.orders.json", extracted);
            var roundTrip = Deploy();
            Assert.That(roundTrip.Audit.Where(r => r.Action is "created" or "modified" or "dropped"), Is.Empty,
                "the extracted table redeploys unchanged: " + roundTrip.DescribeAudit() + "\n" + extracted);
        }
        finally
        {
            Exec(Drops);
        }
    }
}
