// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json.Linq;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Regression coverage for two faces of the same PostgreSQL foreign-key action defect:
//
// 1) ON DELETE/UPDATE SET DEFAULT: pg_constraint.confdeltype/confupdtype have five codes
//    ('a', 'r', 'c', 'n', 'd'), but GenerateTableJson and ModifiedTableQuench mapped only four --
//    'd' (SET DEFAULT) fell through the CASE to NULL. A declared "SET DEFAULT" therefore never
//    matched the extracted/compared NULL, so the FK was dropped and recreated on EVERY quench.
//    Both CASE sites (extraction and the compare-side twin) had the same gap and must agree, so
//    both are exercised here.
// 2) Explicit "NO ACTION": the domain pattern permits it as a literal, but extraction always
//    renders the default action (confdeltype/confupdtype 'a') as ''. Nothing normalized the two
//    spellings together, so a package that spelled it out explicitly churned forever against the
//    '' extraction produces -- the same "declared spelling never matches the extracted spelling"
//    defect, just triggered by an alias instead of a missing catalog code. Fixed by normalizing
//    "NO ACTION" -> '' on the declared side only (ParseTableJsonIntoTempTables.sql), so it's
//    indistinguishable from an omitted action by the time comparison runs and existing '' packages
//    are untouched.
//
// The reliable, rewrite-proof signal is pg_constraint.oid: a drop/recreate of the FK produces a
// NEW oid. A converged, idempotent quench must leave the oid UNCHANGED across a no-op re-quench.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ForeignKeyActionIdempotencyTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_SetDefaultDeleteActionIsIdempotent()
    {
        RunSetDefaultScenario(deleteAction: "SET DEFAULT", updateAction: null, "SetDefaultDel");
    }

    [Test]
    public void TableQuench_SetDefaultUpdateActionIsIdempotent()
    {
        RunSetDefaultScenario(deleteAction: null, updateAction: "SET DEFAULT", "SetDefaultUpd");
    }

    // Guard: adding the 'd' branch, and normalizing the "NO ACTION" alias, must not disturb any
    // other declared spelling. "Omitted" is the shape every existing package on disk uses today
    // and must keep round-tripping through '' unchanged; "NO ACTION" is the explicit alias that
    // was broken (declared "NO ACTION" never matched extracted '') until the parse-side normalize.
    [TestCase(null, TestName = "TableQuench_OmittedDeleteActionRoundTripsUnchanged")]
    [TestCase("NO ACTION", TestName = "TableQuench_ExplicitNoActionDeleteActionRoundTripsUnchanged")]
    [TestCase("RESTRICT", TestName = "TableQuench_RestrictDeleteActionRoundTripsUnchanged")]
    [TestCase("CASCADE", TestName = "TableQuench_CascadeDeleteActionRoundTripsUnchanged")]
    [TestCase("SET NULL", TestName = "TableQuench_SetNullDeleteActionRoundTripsUnchanged")]
    public void TableQuench_OtherForeignKeyActionsRoundTripUnchanged(string deleteAction)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var tableName = $"FkGuard_{uniqueId}";
        var refTableName = $"{tableName}Ref";
        var pkName = $"PK_{tableName}";
        var refPkName = $"PK_{refTableName}";
        var fkName = $"FK_{tableName}_{refTableName}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var deleteActionJson = deleteAction is null ? "" : $@", ""DeleteAction"": ""{deleteAction}""";
        var json = $$"""
[
    {
        "Schema": "{{schema}}",
        "Name": "{{refTableName}}",
        "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ],
        "Indexes": [ { "Name": "{{refPkName}}", "PrimaryKey": true, "IndexColumns": "Id" } ]
    },
    {
        "Schema": "{{schema}}",
        "Name": "{{tableName}}",
        "Columns": [
            { "Name": "Id",       "DataType": "integer", "Nullable": false },
            { "Name": "ParentId", "DataType": "integer", "Nullable": true }
        ],
        "Indexes": [ { "Name": "{{pkName}}", "PrimaryKey": true, "IndexColumns": "Id" } ],
        "ForeignKeys": [
            { "Name": "{{fkName}}", "Columns": "ParentId", "RelatedTableSchema": "{{schema}}", "RelatedTable": "{{refTableName}}", "RelatedColumns": "Id"{{deleteActionJson}} }
        ]
    }
]
""";

        try
        {
            RunTableQuenchProc(cmd, json);

            var oidBefore = GetForeignKeyOid(cmd, schema, tableName, fkName);
            Assert.That(oidBefore, Is.Not.Null.And.Not.EqualTo(0L),
                "Setup failed: the foreign key constraint was not created by the first quench.");

            // No-op re-quench of the identical JSON.
            RunTableQuenchProc(cmd, json);

            var oidAfter = GetForeignKeyOid(cmd, schema, tableName, fkName);
            Assert.That(oidAfter, Is.EqualTo(oidBefore),
                $"Re-quench of DeleteAction '{deleteAction ?? "(omitted)"}' must NOT drop and recreate the " +
                $"foreign key. Before: {oidBefore}, After: {oidAfter}.");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";
DROP TABLE IF EXISTS ""{schema}"".""{refTableName}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private void RunSetDefaultScenario(string deleteAction, string updateAction, string namePrefix)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var tableName = $"Fk{namePrefix}_{uniqueId}";
        var refTableName = $"{tableName}Ref";
        var pkName = $"PK_{tableName}";
        var refPkName = $"PK_{refTableName}";
        var fkName = $"FK_{tableName}_{refTableName}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var deleteActionJson = deleteAction is null ? "" : $@", ""DeleteAction"": ""{deleteAction}""";
        var updateActionJson = updateAction is null ? "" : $@", ""UpdateAction"": ""{updateAction}""";
        var json = $$"""
[
    {
        "Schema": "{{schema}}",
        "Name": "{{refTableName}}",
        "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ],
        "Indexes": [ { "Name": "{{refPkName}}", "PrimaryKey": true, "IndexColumns": "Id" } ]
    },
    {
        "Schema": "{{schema}}",
        "Name": "{{tableName}}",
        "Columns": [
            { "Name": "Id",       "DataType": "integer", "Nullable": false },
            { "Name": "ParentId", "DataType": "integer", "Nullable": true, "Default": "0" }
        ],
        "Indexes": [ { "Name": "{{pkName}}", "PrimaryKey": true, "IndexColumns": "Id" } ],
        "ForeignKeys": [
            { "Name": "{{fkName}}", "Columns": "ParentId", "RelatedTableSchema": "{{schema}}", "RelatedTable": "{{refTableName}}", "RelatedColumns": "Id"{{deleteActionJson}}{{updateActionJson}} }
        ]
    }
]
""";

        try
        {
            // Converge once (with the bug present this first run may still create the FK with the
            // action recorded, since 'd' -> NULL only surfaces as a mismatch on the SECOND compare).
            RunTableQuenchProc(cmd, json);

            var oidBefore = GetForeignKeyOid(cmd, schema, tableName, fkName);
            Assert.That(oidBefore, Is.Not.Null.And.Not.EqualTo(0L),
                "Setup failed: the foreign key constraint was not created by the first quench.");

            var extracted = JObject.Parse(GenerateTableJson(cmd, schema, tableName));
            var extractedFk = (JArray)extracted["ForeignKeys"];
            Assert.That(extractedFk, Has.Count.EqualTo(1), "Expected exactly one extracted foreign key.");
            Assert.That((string)extractedFk[0]["DeleteAction"], Is.EqualTo(deleteAction ?? ""),
                "Extraction must preserve the declared DeleteAction (empty string when unset).");
            Assert.That((string)extractedFk[0]["UpdateAction"], Is.EqualTo(updateAction ?? ""),
                "Extraction must preserve the declared UpdateAction (empty string when unset).");

            // No-op re-quench of the identical JSON: the assertion that would have caught the bug.
            RunTableQuenchProc(cmd, json);

            var oidAfter = GetForeignKeyOid(cmd, schema, tableName, fkName);
            Assert.That(oidAfter, Is.EqualTo(oidBefore),
                $"Re-quench of DeleteAction '{deleteAction ?? "(unset)"}' / UpdateAction '{updateAction ?? "(unset)"}' " +
                $"must NOT drop and recreate the foreign key. A changed pg_constraint.oid proves the FK was dropped " +
                $"and recreated -- the SET DEFAULT mapping gap. Before: {oidBefore}, After: {oidAfter}.");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";
DROP TABLE IF EXISTS ""{schema}"".""{refTableName}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string GenerateTableJson(IDbCommand cmd, string tableSchema, string tableName)
    {
        cmd.CommandText = $@"SELECT ""SchemaSmith"".""GenerateTableJSON""('{tableSchema}', '{tableName}')";
        return (string)cmd.ExecuteScalar();
    }

    private static long? GetForeignKeyOid(IDbCommand cmd, string tableSchema, string tableName, string constraintName)
    {
        cmd.CommandText = $@"
SELECT con.oid
  FROM pg_constraint con
  JOIN pg_class rel ON rel.oid = con.conrelid
  JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
  WHERE nsp.nspname = '{tableSchema}'
    AND rel.relname = '{tableName}'
    AND con.conname = '{constraintName}'
    AND con.contype = 'f';";
        var result = cmd.ExecuteScalar();
        return result is null || result is DBNull ? null : Convert.ToInt64(result);
    }
}
