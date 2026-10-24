// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Repro for #303: WhatIf must be read-only on PostgreSQL. The PG ownership-fixup procs
// (FixupTableOwnership and siblings) ran their ProductOwnership INSERT/DELETE unconditionally —
// the caller did not pass p_WhatIf — so a WhatIf removal really deleted the removed table's
// ProductOwnership row. A subsequent REAL quench then no longer recognized the table as
// product-owned and silently skipped the drop, stranding it. MySQL/SQL Server were unaffected
// (they fixup ownership inside their WhatIf-aware quench procs). Each test owns a unique product
// name so DropTablesRemovedFromProduct is scoped to its own tables under parallel execution.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_WhatIfOwnershipTests : BaseTableQuenchTests
{
    [Test]
    public void WhatIfRemoval_IsReadOnly_AndDoesNotStrandTheTableFromASubsequentRealDrop()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var product = $"WhatIfOwn_{id}";
        var keep = $"WhatIfKeep_{id}";
        var drop = $"WhatIfDrop_{id}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Establish product ownership of both tables.
            RunTableQuenchProc(cmd, TwoTables(schema, keep, drop), productName: product);
            Assert.That(OwnershipRows(cmd, product, schema, drop), Is.EqualTo(1), "Setup: drop table should be product-owned.");

            // WhatIf-remove the drop table. WhatIf must change NOTHING — including ProductOwnership.
            RunTableQuenchProc(cmd, OneTable(schema, keep), dropTablesRemovedFromProduct: true, whatIf: true, productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(ObjectExists(cmd, schema, drop), Is.True, "WhatIf must not drop the table.");
                Assert.That(OwnershipRows(cmd, product, schema, drop), Is.EqualTo(1),
                    "WhatIf must NOT delete the table's ProductOwnership row (#303).");
            });

            // Apply the removal for real. The table must be dropped — a prior WhatIf must not strand it.
            RunTableQuenchProc(cmd, OneTable(schema, keep), dropTablesRemovedFromProduct: true, productName: product);
            Assert.That(ObjectExists(cmd, schema, drop), Is.False,
                "Real quench must drop the removed table even after a preceding WhatIf preview (#303).");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{keep}""; DROP TABLE IF EXISTS ""{schema}"".""{drop}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string TwoTables(string schema, string keep, string drop) => $$"""
[
  { "Schema": "{{schema}}", "Name": "{{keep}}", "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ], "Indexes": [ { "Name": "PK_{{keep}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] },
  { "Schema": "{{schema}}", "Name": "{{drop}}", "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ], "Indexes": [ { "Name": "PK_{{drop}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] }
]
""";

    private static string OneTable(string schema, string keep) => $$"""
[
  { "Schema": "{{schema}}", "Name": "{{keep}}", "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ], "Indexes": [ { "Name": "PK_{{keep}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] }
]
""";

    private static bool ObjectExists(IDbCommand cmd, string schema, string tableName)
    {
        cmd.CommandText = $"SELECT to_regclass('\"{schema}\".\"{tableName}\"') IS NOT NULL";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static int OwnershipRows(IDbCommand cmd, string product, string schema, string tableName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}' AND \"Schema\" = '{schema}' AND \"TableName\" = '{tableName}' AND \"IndexName\" IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
