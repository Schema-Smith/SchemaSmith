// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Drives the PostgreSQL object-change audit weaving (#243 E5). RunTableQuenchProc calls the
// SchemaSmith.TableQuench proc directly (not via DatabaseQuench.Execute), so the audit rows are NOT
// drained by the reader — we query "SchemaSmith"."ChangeAudit" directly on the same session to
// verify each proc emits the right (ObjectType, ObjectName, ActionType) rows as it runs DDL.
[Category("PostgreSQL")]
public class ObjectChangeAuditIntegrationTests : BaseTableQuenchTests
{
    private const string Product = "ObjectChangeAudit Tests";

    // Below PostgreSQL 17 a changed generation expression is applied by dropping and re-adding the column, and the audit
    // left that column out with the ALTER pass's own exclusion, so the change had no row. From 17 it is SET EXPRESSION.
    [Test]
    public void AChangedGenerationExpression_IsAudited_OnEveryVersion()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        static string Json(string expression) => $$"""
            [{ "Schema": "public", "Name": "AuditGenerated",
               "Columns": [ { "Name": "a", "DataType": "integer", "Nullable": true },
                            { "Name": "g", "DataType": "integer", "Nullable": true, "Generated": "ALWAYS", "GenerationExpression": "{{expression}}" } ] }]
            """;
        DropIfExists(cmd, "public", "AuditGenerated");
        try
        {
            RunTableQuenchProc(cmd, Json("a + 1"), productName: Product);
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json("a + 2"), productName: Product);
            Assert.That(ReadAudit(cmd).Count(r => r is { Type: "column", Action: "modified" } && r.Name.EndsWith(".g")), Is.EqualTo(1));
        }
        finally { DropIfExists(cmd, "public", "AuditGenerated"); }
    }

    [Test]
    public void TableQuench_EmitsObjectChangeAudit_ForCreateModifyDrop()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "public", "AuditChild");
        DropIfExists(cmd, "public", "AuditParent");
        DropIfExists(cmd, "public", "AuditDrop");
        ClearAudit(cmd);

        // Phase 1 — create parent + child + a to-be-dropped table.
        RunTableQuenchProc(cmd, CreateJson, productName: Product);
        var created = ReadAudit(cmd);

        Assert.That(created.Any(r => r is { Type: "table", Action: "created" } && r.Name.Contains("AuditParent")),
            "expected table/created for AuditParent");
        Assert.That(created.Any(r => r is { Type: "table", Action: "created" } && r.Name.Contains("AuditChild")),
            "expected table/created for AuditChild");
        Assert.That(created.Any(r => r is { Type: "index", Action: "created" } && r.Name.Contains("IX_AuditChild")),
            "expected index/created for IX_AuditChild_Name");
        Assert.That(created.Any(r => r is { Type: "constraint", Action: "created" } && r.Name.Contains("PK_AuditChild")),
            "expected constraint/created for the primary key");
        Assert.That(created.Any(r => r is { Type: "constraint", Action: "created" } && r.Name.Contains("CK_AuditChild")),
            "expected constraint/created for the check constraint");
        Assert.That(created.Any(r => r is { Type: "foreignKey", Action: "created" } && r.Name.Contains("FK_AuditChild")),
            "expected foreignKey/created for FK_AuditChild_Parent");

        ClearAudit(cmd);

        // Phase 2 — widen Name (column modify); drop the index, FK, and check by absence; drop the
        // AuditDrop table (removed from product).
        RunTableQuenchProc(cmd, ModifyJson, productName: Product, dropTablesRemovedFromProduct: true);
        var changed = ReadAudit(cmd);

        Assert.That(changed.Any(r => r is { Type: "column", Action: "modified" } && r.Name.Contains("Name")),
            "expected column/modified for Name");
        Assert.That(changed.Any(r => r is { Type: "index", Action: "dropped" } && r.Name.Contains("IX_AuditChild")),
            "expected index/dropped for IX_AuditChild_Name");
        Assert.That(changed.Any(r => r is { Type: "foreignKey", Action: "dropped" }),
            "expected foreignKey/dropped for FK_AuditChild_Parent");
        Assert.That(changed.Any(r => r is { Type: "constraint", Action: "dropped" } && r.Name.Contains("CK_AuditChild")),
            "expected constraint/dropped for the check constraint");
        Assert.That(changed.Any(r => r is { Type: "table", Action: "dropped" } && r.Name.Contains("AuditDrop")),
            "expected table/dropped for AuditDrop");
        Assert.That(changed.Any(r => r is { Type: "column", Action: "created" } && r.Name.Contains("Calc")),
            "expected column/created for the computed column Calc added to the existing table");

        DropIfExists(cmd, "public", "AuditChild");
        DropIfExists(cmd, "public", "AuditParent");
        DropIfExists(cmd, "public", "AuditDrop");
        conn.Close();
    }

    [Test]
    public void TableQuench_WhatIf_EmitsWouldChangeAudit_WithoutRunningDdl()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "public", "AuditChild");
        DropIfExists(cmd, "public", "AuditParent");
        DropIfExists(cmd, "public", "AuditDrop");
        DropIfExists(cmd, "public", "AuditNew");
        ClearAudit(cmd);

        // Baseline: really create the parent + child + a to-be-dropped table.
        RunTableQuenchProc(cmd, CreateJson, productName: Product);
        ClearAudit(cmd);

        // WhatIf re-quench (#363): add a new table + a new column (wouldCreate), widen Name
        // (wouldModify), and drop the index/FK/check by absence + AuditDrop by absence (wouldDrop).
        RunTableQuenchProc(cmd, WhatIfJson, productName: Product, whatIf: true, dropTablesRemovedFromProduct: true);
        var wi = ReadAudit(cmd);

        Assert.Multiple(() =>
        {
            Assert.That(wi.Any(r => r is { Type: "table", Action: "wouldCreate" } && r.Name.Contains("AuditNew")),
                "expected table/wouldCreate for AuditNew");
            Assert.That(wi.Any(r => r is { Type: "column", Action: "wouldCreate" } && r.Name.Contains("Extra")),
                "expected column/wouldCreate for the new Extra column");
            Assert.That(wi.Any(r => r is { Type: "index", Action: "wouldCreate" } && r.Name.Contains("IX_AuditChild_Extra")),
                "expected index/wouldCreate for the new IX_AuditChild_Extra");
            Assert.That(wi.Any(r => r is { Type: "constraint", Action: "wouldCreate" } && r.Name.Contains("PK_AuditNew")),
                "expected constraint/wouldCreate for PK_AuditNew");
            Assert.That(wi.Any(r => r is { Type: "column", Action: "wouldModify" } && r.Name.Contains("Name")),
                "expected column/wouldModify for the widened Name column");
            Assert.That(wi.Any(r => r is { Type: "index", Action: "wouldDrop" } && r.Name.Contains("IX_AuditChild_Name")),
                "expected index/wouldDrop for IX_AuditChild_Name");
            Assert.That(wi.Any(r => r is { Type: "foreignKey", Action: "wouldDrop" }),
                "expected foreignKey/wouldDrop for FK_AuditChild_Parent");
            Assert.That(wi.Any(r => r is { Type: "table", Action: "wouldDrop" } && r.Name.Contains("AuditDrop")),
                "expected table/wouldDrop for AuditDrop");

            Assert.That(wi.All(r => r.Action is not ("created" or "modified" or "dropped")),
                "WhatIf must not emit executed-change actions");
        });

        cmd.CommandText = "SELECT to_regclass('public.\"AuditNew\"')::text";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(System.DBNull.Value), "WhatIf must not create AuditNew");
        cmd.CommandText = "SELECT to_regclass('public.\"AuditDrop\"')::text";
        Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(System.DBNull.Value), "WhatIf must not drop AuditDrop");
        cmd.CommandText = "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'AuditChild' AND column_name = 'Name'";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(50), "WhatIf must not widen Name (VARCHAR(50))");

        DropIfExists(cmd, "public", "AuditChild");
        DropIfExists(cmd, "public", "AuditParent");
        DropIfExists(cmd, "public", "AuditDrop");
        conn.Close();
    }

    private const string WhatIfJson = """
        [
        {
            "Schema": "public", "Name": "AuditParent",
            "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false } ],
            "Indexes": [ { "Name": "PK_AuditParent", "PrimaryKey": true, "IndexColumns": "Id" } ]
        },
        {
            "Schema": "public", "Name": "AuditChild",
            "Columns": [
                { "Name": "Id", "DataType": "INT4", "Nullable": false },
                { "Name": "ParentId", "DataType": "INT4", "Nullable": false },
                { "Name": "Name", "DataType": "VARCHAR(100)", "Nullable": true },
                { "Name": "Extra", "DataType": "INT4", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "PK_AuditChild", "PrimaryKey": true, "IndexColumns": "Id" },
                { "Name": "IX_AuditChild_Extra", "IndexColumns": "Extra" }
            ]
        },
        {
            "Schema": "public", "Name": "AuditNew",
            "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false } ],
            "Indexes": [ { "Name": "PK_AuditNew", "PrimaryKey": true, "IndexColumns": "Id" } ]
        }
        ]
        """;

    private const string CreateJson = """
        [
        {
            "Schema": "public", "Name": "AuditParent",
            "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false } ],
            "Indexes": [ { "Name": "PK_AuditParent", "PrimaryKey": true, "IndexColumns": "Id" } ]
        },
        {
            "Schema": "public", "Name": "AuditChild",
            "Columns": [
                { "Name": "Id", "DataType": "INT4", "Nullable": false },
                { "Name": "ParentId", "DataType": "INT4", "Nullable": false },
                { "Name": "Name", "DataType": "VARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "PK_AuditChild", "PrimaryKey": true, "IndexColumns": "Id" },
                { "Name": "IX_AuditChild_Name", "IndexColumns": "Name" }
            ],
            "ForeignKeys": [
                { "Name": "FK_AuditChild_Parent", "Columns": "ParentId", "RelatedTable": "AuditParent", "RelatedTableSchema": "public", "RelatedColumns": "Id" }
            ],
            "CheckConstraints": [
                { "Name": "CK_AuditChild_Ids", "Expression": "\"Id\" <> \"ParentId\"" }
            ]
        },
        {
            "Schema": "public", "Name": "AuditDrop",
            "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false } ],
            "Indexes": [ { "Name": "PK_AuditDrop", "PrimaryKey": true, "IndexColumns": "Id" } ]
        }
        ]
        """;

    private const string ModifyJson = """
        [
        {
            "Schema": "public", "Name": "AuditParent",
            "Columns": [ { "Name": "Id", "DataType": "INT4", "Nullable": false } ],
            "Indexes": [ { "Name": "PK_AuditParent", "PrimaryKey": true, "IndexColumns": "Id" } ]
        },
        {
            "Schema": "public", "Name": "AuditChild",
            "Columns": [
                { "Name": "Id", "DataType": "INT4", "Nullable": false },
                { "Name": "ParentId", "DataType": "INT4", "Nullable": false },
                { "Name": "Name", "DataType": "VARCHAR(100)", "Nullable": true },
                { "Name": "Calc", "DataType": "INT4", "ComputedExpression": "\"Id\" + \"ParentId\"" }
            ],
            "Indexes": [ { "Name": "PK_AuditChild", "PrimaryKey": true, "IndexColumns": "Id" } ]
        }
        ]
        """;

    private static void DropIfExists(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS \"{schema}\".\"{table}\" CASCADE";
        cmd.ExecuteNonQuery();
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()";
        cmd.ExecuteNonQuery();
    }

    private static List<AuditRow> ReadAudit(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT \"ObjectType\", \"ObjectName\", \"ActionType\" FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() ORDER BY \"Id\"";
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuditRow>();
        while (reader.Read())
            rows.Add(new AuditRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private sealed record AuditRow(string Type, string Name, string Action);
}
