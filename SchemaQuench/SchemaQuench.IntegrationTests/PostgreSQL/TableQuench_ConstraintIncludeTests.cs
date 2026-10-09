// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// PG-031. A primary key or unique constraint declared with INCLUDE columns or storage parameters was added without
// them, so the deployed constraint never matched its declaration: every deploy dropped it CASCADE, taking the foreign
// keys that reference it, and added it back. The constraint must carry both, and a redeploy must touch nothing.
[Category("PostgreSQL")]
public class TableQuench_ConstraintIncludeTests : BaseTableQuenchTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void AConstraintWithIncludeAndStorageParameters_DeploysWithThem_AndTheRedeployKeepsItsForeignKeys(bool indexOnly)
    {
        var schema = "ConstraintInclude" + (indexOnly ? "IdxOnly" : "Full");
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Run(cmd, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE; CREATE SCHEMA ""{schema}"";");
        try
        {
            cmd.CommandText = "SELECT current_setting('server_version_num')::int";
            // deduplicate_items is a btree storage parameter from PostgreSQL 13; 12 has none but fillfactor.
            var storage = Convert.ToInt32(cmd.ExecuteScalar()) >= 130000 ? @", ""StorageParameters"": { ""deduplicate_items"": ""off"" }" : "";
            var json = $$"""
                [
                { "Schema": "{{schema}}", "Name": "U",
                  "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "K", "DataType": "INT", "Nullable": false },
                               { "Name": "V", "DataType": "INT", "Nullable": true } ],
                  "Indexes": [ { "Name": "PK_U", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id", "IncludeColumns": "V" },
                               { "Name": "U_K", "UniqueConstraint": true, "Unique": true, "IndexColumns": "K", "IncludeColumns": "V", "FillFactor": 80{{storage}} } ] },
                { "Schema": "{{schema}}", "Name": "R",
                  "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "KRef", "DataType": "INT", "Nullable": true } ],
                  "Indexes": [ { "Name": "PK_R", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
                  "ForeignKeys": [ { "Name": "FK_R_U", "Columns": "KRef", "RelatedTableSchema": "{{schema}}", "RelatedTable": "U", "RelatedColumns": "K" } ] }
                ]
                """;
            if (indexOnly)
            {
                Run(cmd, $@"CREATE TABLE ""{schema}"".""U"" (""Id"" INT NOT NULL, ""K"" INT NOT NULL, ""V"" INT);
                            CREATE TABLE ""{schema}"".""R"" (""Id"" INT NOT NULL CONSTRAINT ""PK_R"" PRIMARY KEY, ""KRef"" INT);");
                RunTableQuenchProc(cmd, json, indexOnly: true);
                Run(cmd, $@"ALTER TABLE ""{schema}"".""R"" ADD CONSTRAINT ""FK_R_U"" FOREIGN KEY (""KRef"") REFERENCES ""{schema}"".""U"" (""K"")");
            }
            else
                RunTableQuenchProc(cmd, json);

            var fkBefore = ForeignKeyOid(cmd, schema);
            Assert.That(fkBefore, Is.Not.Null, "precondition: the foreign key on the unique constraint exists");
            RunTableQuenchProc(cmd, json, indexOnly: indexOnly);

            Assert.Multiple(() =>
            {
                Assert.That(Definition(cmd, schema, "PK_U"), Does.Contain("INCLUDE (\"V\")").IgnoreCase.Or.Contain("INCLUDE (v)").IgnoreCase,
                    "the primary key must carry its INCLUDE column");
                Assert.That(Definition(cmd, schema, "U_K"), Does.Contain("INCLUDE (").IgnoreCase, "the unique constraint must carry its INCLUDE column");
                Assert.That(IndexOptions(cmd, schema, "U_K"), Does.Contain("fillfactor=80"));
                if (storage != "")
                    Assert.That(IndexOptions(cmd, schema, "U_K"), Does.Contain("deduplicate_items=off"), "and its storage parameters");
                Assert.That(ForeignKeyOid(cmd, schema), Is.EqualTo(fkBefore),
                    "the redeploy must not drop the constraint, which would take the foreign key with it");
            });
        }
        finally
        {
            Run(cmd, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;");
        }
    }

    private static string Definition(IDbCommand cmd, string schema, string constraint)
    {
        cmd.CommandText = $@"SELECT pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
                              WHERE n.nspname = '{schema}' AND c.conname = '{constraint}'";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static string IndexOptions(IDbCommand cmd, string schema, string index)
    {
        cmd.CommandText = $@"SELECT array_to_string(cl.reloptions, ',') FROM pg_class cl JOIN pg_namespace n ON n.oid = cl.relnamespace
                              WHERE n.nspname = '{schema}' AND cl.relname = '{index}'";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static object ForeignKeyOid(IDbCommand cmd, string schema)
    {
        cmd.CommandText = $@"SELECT c.oid::bigint FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
                              WHERE n.nspname = '{schema}' AND c.conname = 'FK_R_U'";
        return cmd.ExecuteScalar();
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
