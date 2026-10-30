// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

// Drives the MySQL object-change audit weaving (#243 E5). RunTableQuenchProc calls the
// SchemaSmith_TableQuench proc directly (not via DatabaseQuench.Execute), so the audit rows are NOT
// drained by the reader — we query SchemaSmith_ChangeAudit directly on the same session to verify
// each proc emits the right (ObjectType, ObjectName, ActionType) rows as it runs DDL.
public abstract class ObjectChangeAuditIntegrationTestsSharedTests : BaseTableQuenchTests
{
    private const string Product = "ObjectChangeAudit Tests";

    [Test]
    public void TableQuench_EmitsObjectChangeAudit_ForCreateModifyDrop()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("Exercises CHECK-constraint audit rows; CHECK requires MySQL 8.0.16 — skipped below the floor.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "AuditChild");
        DropIfExists(cmd, "AuditParent");
        DropIfExists(cmd, "AuditDrop");
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
        Assert.That(created.Any(r => r is { Type: "constraint", Action: "created" } && r.Name.Contains("CK_AuditChild")),
            "expected constraint/created for the check constraint");
        Assert.That(created.Any(r => r is { Type: "foreignKey", Action: "created" } && r.Name.Contains("FK_AuditChild")),
            "expected foreignKey/created for FK_AuditChild_Parent");

        ClearAudit(cmd);

        // Phase 2 — widen Name (column modify); drop the index + FK by absence; drop the AuditDrop
        // table (removed from product).
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
            "expected column/created for the generated column Calc added to the existing table");

        DropIfExists(cmd, "AuditChild");
        DropIfExists(cmd, "AuditParent");
        DropIfExists(cmd, "AuditDrop");
        conn.Close();
    }

    [Test]
    public void TableQuench_WhatIf_EmitsWouldChangeAudit_WithoutRunningDdl()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "AuditChild");
        DropIfExists(cmd, "AuditParent");
        DropIfExists(cmd, "AuditDrop");
        DropIfExists(cmd, "AuditNew");
        ClearAudit(cmd);

        // Baseline: really create parent + child + a to-be-dropped table.
        RunTableQuenchProc(cmd, CreateJson, productName: Product);
        ClearAudit(cmd);

        // WhatIf re-quench (#363): add a new table + column (wouldCreate), widen Name (wouldModify),
        // drop the index/FK by absence + AuditDrop by absence (wouldDrop).
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

        cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AuditNew'";
        Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "WhatIf must not create AuditNew");
        cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AuditDrop'";
        Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "WhatIf must not drop AuditDrop");
        cmd.CommandText = "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AuditChild' AND COLUMN_NAME = 'Name'";
        Assert.That(System.Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(50), "WhatIf must not widen Name (VARCHAR(50))");

        DropIfExists(cmd, "AuditChild");
        DropIfExists(cmd, "AuditParent");
        DropIfExists(cmd, "AuditDrop");
        conn.Close();
    }

    // A declared index rename must stay a RENAME. The end state of a rename and of a drop-then-recreate
    // is identical -- new name present, old name absent -- so TableQuench_ShouldRenameIndex passes over
    // the difference, and the difference is the whole point: a rename is metadata, a recreate is a full
    // index build plus a window with no index. The audit trail is where a user actually sees which one
    // happened, so that is what this asserts.
    //
    // Guards the index-drop placement move specifically. Index removal currently runs AFTER rename
    // detection inside MissingIndexesAndConstraintsQuench; moving it to ModifiedTableQuench puts it
    // BEFORE, where _SchemaSmith_IndexRenames does not exist yet and ProductOwnership still carries the
    // OLD index name -- so the removal-by-absence axis would happily drop an index that was only
    // being renamed.
    [Test]
    public void TableQuench_RenamingAnIndex_DoesNotReportItAsDropped()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        DropIfExists(cmd, "AuditRename");
        ClearAudit(cmd);

        // Deploy the index under its first name so SchemaSmith owns it -- ownership is what makes the
        // removal-by-absence axis consider it at all, so without this the test could not fail.
        RunTableQuenchProc(cmd, RenameBeforeJson, productName: Product);
        Assert.That(ReadAudit(cmd).Any(r => r is { Type: "index", Action: "created" } && r.Name.Contains("IX_AuditRename_Before")),
            "setup: the index must have been created and owned before the rename is exercised");

        ClearAudit(cmd);

        // Same columns, new name -- the shape rename detection exists for.
        RunTableQuenchProc(cmd, RenameAfterJson, productName: Product);
        var after = ReadAudit(cmd);

        Assert.Multiple(() =>
        {
            Assert.That(after.Any(r => r is { Type: "index", Action: "dropped" } && r.Name.Contains("IX_AuditRename_Before")), Is.False,
                "a renamed index must not be reported as dropped -- that means it was dropped and rebuilt, "
                + "paying a full index build instead of a metadata rename");
            Assert.That(after.Any(r => r is { Type: "index", Action: "created" } && r.Name.Contains("IX_AuditRename_After")), Is.False,
                "nor recreated under the new name -- a rename creates nothing");
        });

        cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE()"
                        + " AND TABLE_NAME = 'AuditRename' AND INDEX_NAME = 'IX_AuditRename_After'";
        Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0),
            "and the rename must actually have happened");

        DropIfExists(cmd, "AuditRename");
        conn.Close();
    }

    // Every row names an object as the catalog does, without backticks, whether it was created or modified, and the
    // progress log gives one line for what runs as one ALTER.
    [Test]
    public void TableQuench_NamesEveryChangeOneWay_AndLogsAFoldedAlterOnce()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        DropIfExists(cmd, "AuditSpell");
        ClearAudit(cmd);
        string Json(string length, string extra) => $$"""
            [ { "Name": "`AuditSpell`",
                "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                             { "Name": "`A`", "DataType": "VARCHAR({{length}})", "Nullable": true },
                             { "Name": "`B`", "DataType": "VARCHAR({{length}})", "Nullable": true }{{extra}} ],
                "Indexes": [ { "Name": "`PK_AuditSpell`", "PrimaryKey": true, "IndexColumns": "`Id`" } ] } ]
            """;
        try
        {
            RunTableQuenchProc(cmd, Json("10", ""), productName: Product);
            var created = ReadAudit(cmd);
            ClearAudit(cmd);
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json("20", """, { "Name": "`C`", "DataType": "INT", "Nullable": true }"""), productName: Product);
            var changed = ReadAudit(cmd);
            cmd.CommandText = "SELECT Message FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%Modify column:%AuditSpell%'";
            var modifyLines = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) modifyLines.Add(reader.GetString(0));

            Assert.Multiple(() =>
            {
                Assert.That(created.Where(r => r.Type == "table").Select(r => r.Name), Is.EqualTo(new[] { "AuditSpell" }));
                Assert.That(changed.Where(r => r.Type == "column").Select(r => $"{r.Name}|{r.Action}"),
                    Is.EquivalentTo(new[] { "AuditSpell.C|created", "AuditSpell.A|modified", "AuditSpell.B|modified" }));
                Assert.That(modifyLines, Has.Count.EqualTo(1), "one ALTER, one line");
                Assert.That(modifyLines.FirstOrDefault(), Does.Contain("MODIFY COLUMN `A`").And.Contain("MODIFY COLUMN `B`"));
            });
        }
        finally
        {
            DropIfExists(cmd, "AuditSpell");
        }
        conn.Close();
    }

    private const string RenameBeforeJson = """
        [
        {
            "Name": "`AuditRename`",
            "Columns": [
                { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                { "Name": "`Label`", "DataType": "VARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "`PK_AuditRename`", "PrimaryKey": true, "IndexColumns": "`Id`" },
                { "Name": "`IX_AuditRename_Before`", "IndexColumns": "`Label`", "Unique": false, "PrimaryKey": false }
            ]
        }
        ]
        """;

    private const string RenameAfterJson = """
        [
        {
            "Name": "`AuditRename`",
            "Columns": [
                { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                { "Name": "`Label`", "DataType": "VARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "`PK_AuditRename`", "PrimaryKey": true, "IndexColumns": "`Id`" },
                { "Name": "`IX_AuditRename_After`", "IndexColumns": "`Label`", "Unique": false, "PrimaryKey": false }
            ]
        }
        ]
        """;

    private const string CreateJson = """
        [
        {
            "Name": "`AuditParent`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "`PK_AuditParent`", "PrimaryKey": true, "IndexColumns": "`Id`" } ]
        },
        {
            "Name": "`AuditChild`",
            "Columns": [
                { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                { "Name": "`ParentId`", "DataType": "INT", "Nullable": false },
                { "Name": "`Name`", "DataType": "VARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "`PK_AuditChild`", "PrimaryKey": true, "IndexColumns": "`Id`" },
                { "Name": "`IX_AuditChild_Name`", "IndexColumns": "`Name`", "Unique": false, "PrimaryKey": false }
            ],
            "ForeignKeys": [
                { "Name": "`FK_AuditChild_Parent`", "Columns": "`ParentId`", "RelatedTable": "`AuditParent`", "RelatedColumns": "`Id`" }
            ],
            "CheckConstraints": [
                { "Name": "`CK_AuditChild_Ids`", "Expression": "`Id` <> `ParentId`" }
            ]
        },
        {
            "Name": "`AuditDrop`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "`PK_AuditDrop`", "PrimaryKey": true, "IndexColumns": "`Id`" } ]
        }
        ]
        """;

    private const string ModifyJson = """
        [
        {
            "Name": "`AuditParent`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "`PK_AuditParent`", "PrimaryKey": true, "IndexColumns": "`Id`" } ]
        },
        {
            "Name": "`AuditChild`",
            "Columns": [
                { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                { "Name": "`ParentId`", "DataType": "INT", "Nullable": false },
                { "Name": "`Name`", "DataType": "VARCHAR(100)", "Nullable": true },
                { "Name": "`Calc`", "DataType": "INT", "GeneratedExpression": "`Id` + `ParentId`" }
            ],
            "Indexes": [ { "Name": "`PK_AuditChild`", "PrimaryKey": true, "IndexColumns": "`Id`" } ]
        }
        ]
        """;

    private const string WhatIfJson = """
        [
        {
            "Name": "`AuditParent`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "`PK_AuditParent`", "PrimaryKey": true, "IndexColumns": "`Id`" } ]
        },
        {
            "Name": "`AuditChild`",
            "Columns": [
                { "Name": "`Id`", "DataType": "INT", "Nullable": false },
                { "Name": "`ParentId`", "DataType": "INT", "Nullable": false },
                { "Name": "`Name`", "DataType": "VARCHAR(100)", "Nullable": true },
                { "Name": "`Extra`", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "`PK_AuditChild`", "PrimaryKey": true, "IndexColumns": "`Id`" },
                { "Name": "`IX_AuditChild_Extra`", "IndexColumns": "`Extra`", "Unique": false, "PrimaryKey": false }
            ]
        },
        {
            "Name": "`AuditNew`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "`PK_AuditNew`", "PrimaryKey": true, "IndexColumns": "`Id`" } ]
        }
        ]
        """;

    private static void DropIfExists(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
        cmd.ExecuteNonQuery();
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private static List<AuditRow> ReadAudit(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT ObjectType, ObjectName, ActionType FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() ORDER BY Id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuditRow>();
        while (reader.Read())
            rows.Add(new AuditRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private sealed record AuditRow(string Type, string Name, string Action);
}
