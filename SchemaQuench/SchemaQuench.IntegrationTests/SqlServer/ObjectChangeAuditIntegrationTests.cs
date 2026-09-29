// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Drives the SQL Server object-change audit weaving (#243 E5). RunTableQuenchProc calls the
// SchemaSmith.TableQuench proc directly (not via DatabaseQuench.Execute), so the audit rows are NOT
// drained by the reader — we query SchemaSmith.ChangeAudit directly on the same session to verify
// each proc emits the right (ObjectType, ObjectName, ActionType) rows as it runs DDL.
[Category("SqlServer")]
public class ObjectChangeAuditIntegrationTests : BaseTableQuenchTests
{
    private const string Product = "ObjectChangeAudit Tests";

    [Test]
    public void TableQuench_EmitsObjectChangeAudit_ForCreateModifyDrop()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "dbo.AuditChild");
        DropIfExists(cmd, "dbo.AuditParent");
        DropIfExists(cmd, "dbo.AuditDrop");
        ClearAudit(cmd);

        // Phase 1 — create parent + child + a to-be-dropped table: exercises table create, column
        // create, index create, PK/constraint create, check-constraint create, and FK create across
        // all four table procs.
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
        Assert.That(created.Any(r => r is { Type: "column", Action: "created" } && r.Name.Contains("Calc")),
            "expected column/created for the computed column Calc");
        Assert.That(created.Any(r => r is { Type: "statistic", Action: "created" } && r.Name.Contains("ST_AuditChild")),
            "expected statistic/created for ST_AuditChild_Id");

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

        DropIfExists(cmd, "dbo.AuditChild");
        DropIfExists(cmd, "dbo.AuditParent");
        DropIfExists(cmd, "dbo.AuditDrop");
        conn.Close();
    }

    [Test]
    public void TableQuench_WhatIf_EmitsWouldChangeAudit_WithoutRunningDdl()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "dbo.AuditChild");
        DropIfExists(cmd, "dbo.AuditParent");
        DropIfExists(cmd, "dbo.AuditDrop");
        DropIfExists(cmd, "dbo.AuditNew");
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
            Assert.That(wi.Any(r => r is { Type: "column", Action: "wouldModify" } && r.Name.Contains("Name")),
                "expected column/wouldModify for the widened Name column");
            Assert.That(wi.Any(r => r is { Type: "index", Action: "wouldCreate" } && r.Name.Contains("IX_AuditChild_Extra")),
                "expected index/wouldCreate for the new IX_AuditChild_Extra");
            Assert.That(wi.Any(r => r is { Type: "constraint", Action: "wouldCreate" } && r.Name.Contains("PK_AuditNew")),
                "expected constraint/wouldCreate for PK_AuditNew");
            Assert.That(wi.Any(r => r is { Type: "index", Action: "wouldDrop" } && r.Name.Contains("IX_AuditChild_Name")),
                "expected index/wouldDrop for IX_AuditChild_Name");
            Assert.That(wi.Any(r => r is { Type: "foreignKey", Action: "wouldDrop" }),
                "expected foreignKey/wouldDrop for FK_AuditChild_Parent");
            Assert.That(wi.Any(r => r is { Type: "table", Action: "wouldDrop" } && r.Name.Contains("AuditDrop")),
                "expected table/wouldDrop for AuditDrop");

            // WhatIf must not touch the schema: no real actions, and the DDL did not run.
            Assert.That(wi.All(r => r.Action is not ("created" or "modified" or "dropped")),
                "WhatIf must not emit executed-change actions");
        });

        cmd.CommandText = "SELECT OBJECT_ID('dbo.AuditNew')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(System.DBNull.Value), "WhatIf must not create AuditNew");
        cmd.CommandText = "SELECT OBJECT_ID('dbo.AuditDrop')";
        Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(System.DBNull.Value), "WhatIf must not drop AuditDrop");
        cmd.CommandText = "SELECT CAST(max_length AS INT) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AuditChild') AND name = 'Name'";
        Assert.That((int)cmd.ExecuteScalar(), Is.EqualTo(100), "WhatIf must not widen Name (NVARCHAR(50) => 100 bytes)");

        DropIfExists(cmd, "dbo.AuditChild");
        DropIfExists(cmd, "dbo.AuditParent");
        DropIfExists(cmd, "dbo.AuditDrop");
        conn.Close();
    }

    private const string CreateJson = """
        [
        {
            "Schema": "[dbo]", "Name": "[AuditParent]",
            "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "[PK_AuditParent]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
        },
        {
            "Schema": "[dbo]", "Name": "[AuditChild]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[ParentId]", "DataType": "INT", "Nullable": false },
                { "Name": "[Name]", "DataType": "NVARCHAR(50)", "Nullable": true },
                { "Name": "[Calc]", "DataType": "INT", "ComputedExpression": "[Id]+[ParentId]" }
            ],
            "Indexes": [
                { "Name": "[PK_AuditChild]", "PrimaryKey": true, "IndexColumns": "[Id]" },
                { "Name": "[IX_AuditChild_Name]", "IndexColumns": "[Name]" }
            ],
            "ForeignKeys": [
                { "Name": "[FK_AuditChild_Parent]", "Columns": "[ParentId]", "RelatedTable": "[AuditParent]", "RelatedTableSchema": "[dbo]", "RelatedColumns": "[Id]" }
            ],
            "CheckConstraints": [
                { "Name": "CK_AuditChild_Ids", "Expression": "[Id]<>[ParentId]" }
            ],
            "Statistics": [
                { "Name": "ST_AuditChild_Id", "Columns": "[Id]" }
            ]
        },
        {
            "Schema": "[dbo]", "Name": "[AuditDrop]",
            "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "[PK_AuditDrop]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
        }
        ]
        """;

    private const string ModifyJson = """
        [
        {
            "Schema": "[dbo]", "Name": "[AuditParent]",
            "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "[PK_AuditParent]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
        },
        {
            "Schema": "[dbo]", "Name": "[AuditChild]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[ParentId]", "DataType": "INT", "Nullable": false },
                { "Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": true },
                { "Name": "[Calc]", "DataType": "INT", "ComputedExpression": "[Id]+[ParentId]" }
            ],
            "Indexes": [ { "Name": "[PK_AuditChild]", "PrimaryKey": true, "IndexColumns": "[Id]" } ],
            "Statistics": [
                { "Name": "ST_AuditChild_Id", "Columns": "[Id]" }
            ]
        }
        ]
        """;

    private const string WhatIfJson = """
        [
        {
            "Schema": "[dbo]", "Name": "[AuditParent]",
            "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "[PK_AuditParent]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
        },
        {
            "Schema": "[dbo]", "Name": "[AuditChild]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[ParentId]", "DataType": "INT", "Nullable": false },
                { "Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": true },
                { "Name": "[Calc]", "DataType": "INT", "ComputedExpression": "[Id]+[ParentId]" },
                { "Name": "[Extra]", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "[PK_AuditChild]", "PrimaryKey": true, "IndexColumns": "[Id]" },
                { "Name": "[IX_AuditChild_Extra]", "IndexColumns": "[Extra]" }
            ],
            "Statistics": [ { "Name": "ST_AuditChild_Id", "Columns": "[Id]" } ]
        },
        {
            "Schema": "[dbo]", "Name": "[AuditNew]",
            "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "[PK_AuditNew]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
        }
        ]
        """;

    private static void DropIfExists(IDbCommand cmd, string qualifiedName)
    {
        cmd.CommandText = $"IF OBJECT_ID('{qualifiedName}') IS NOT NULL DROP TABLE {qualifiedName}";
        cmd.ExecuteNonQuery();
    }

    // A check constraint re-applied because its expression changed, or dropped because a column it reads changed,
    // was dropped by DDL that wrote no audit row -- so the deployment summary reported the re-create and never the
    // drop. Only the removed-from-product path audited. PostgreSQL audits all three.
    [TestCase(false, "dropped")]
    [TestCase(true, "wouldDrop")]
    public void AModifiedCheckExpression_AuditsItsDrop(bool whatIf, string expectedAction)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        DropIfExists(cmd, "dbo.AuditCheckMod");
        try
        {
            RunTableQuenchProc(cmd, CheckJson("[Qty] <= 10"), productName: Product);
            ClearAudit(cmd);

            RunTableQuenchProc(cmd, CheckJson("[Qty] <= 20"), whatIf: whatIf, productName: Product);

            Assert.That(ReadAudit(cmd).Any(r => r.Type == "constraint" && r.Action == expectedAction && r.Name.Contains("CK_AuditCheckMod")),
                $"expected constraint/{expectedAction} for the re-applied check; got: "
                + string.Join("; ", ReadAudit(cmd).Select(r => $"{r.Type}/{r.Action}/{r.Name}")));
        }
        finally { DropIfExists(cmd, "dbo.AuditCheckMod"); }
    }

    [Test]
    public void ACheckDroppedBecauseItsColumnChanged_AuditsTheDrop()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        DropIfExists(cmd, "dbo.AuditCheckMod");
        try
        {
            RunTableQuenchProc(cmd, CheckJson("[Qty] <= 10"), productName: Product);
            ClearAudit(cmd);

            RunTableQuenchProc(cmd, CheckJson("[Qty] <= 10", qtyType: "BIGINT"), productName: Product);

            Assert.That(ReadAudit(cmd).Any(r => r is { Type: "constraint", Action: "dropped" } && r.Name.Contains("CK_AuditCheckMod")),
                "expected constraint/dropped for the check dropped ahead of its column's type change; got: "
                + string.Join("; ", ReadAudit(cmd).Select(r => $"{r.Type}/{r.Action}/{r.Name}")));
        }
        finally { DropIfExists(cmd, "dbo.AuditCheckMod"); }
    }

    private static string CheckJson(string expression, string qtyType = "INT") => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[AuditCheckMod]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Qty]", "DataType": "{{qtyType}}", "Nullable": false }
            ],
            "CheckConstraints": [
                { "Name": "[CK_AuditCheckMod_Qty]", "Expression": "{{expression}}" }
            ]
        }
        """;

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private static List<AuditRow> ReadAudit(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT ObjectType, ObjectName, ActionType FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID ORDER BY Id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuditRow>();
        while (reader.Read())
            rows.Add(new AuditRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private sealed record AuditRow(string Type, string Name, string Action);
}
