// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Regression coverage for #289: dropping a table removed from the product must first drop any
// foreign key that REFERENCES it. A kept table keeps its FK columns, but the product no longer
// declares the FK and no longer declares the referenced table. Before the fix, the removed-table
// drop ran before the FK drop, so SQL Server aborted the quench with
// "Could not drop object ... because it is referenced by a FOREIGN KEY constraint".
//
// Each test owns a UNIQUE product name so DropTablesRemovedFromProduct is scoped to its own tables
// and never drops a sibling test's tables under parallel execution.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_RemovedTableInboundFkTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_DropsInboundForeignKeyBeforeDroppingRemovedTable()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"InboundFkProduct_{uniqueId}";
        var parent = $"InboundFkParent_{uniqueId}"; // referenced table, removed in the second quench
        var child = $"InboundFkChild_{uniqueId}";    // kept table that holds the inbound FK
        var fkName = $"FK_{child}_{parent}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Establish product ownership of both tables, with the inbound FK in place.
            RunTableQuenchProc(cmd, WithParentAndFk(parent, child, fkName), productName: product);
            Assert.That(ObjectExists(cmd, parent), Is.True, "Setup: parent table should exist after the first quench.");
            Assert.That(ForeignKeyExists(cmd, fkName), Is.True, "Setup: inbound FK should exist after the first quench.");

            // Remove the parent table AND the child's FK from the product, with autodrop on.
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, ChildOnly(child), dropTablesRemovedFromProduct: true, productName: product),
                "Quench must drop the inbound FK before dropping the removed table (#289).");

            Assert.Multiple(() =>
            {
                Assert.That(ObjectExists(cmd, parent), Is.False, "Removed parent table should be dropped.");
                Assert.That(ForeignKeyExists(cmd, fkName), Is.False, "Inbound FK should be dropped.");
                Assert.That(ObjectExists(cmd, child), Is.True, "Kept child table must survive.");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{child}]; DROP TABLE IF EXISTS [dbo].[{parent}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // WhatIf must show the same picture as apply: it previews BOTH the inbound-FK drop and the
    // removed-table drop, and changes nothing.
    [Test]
    public void TableQuench_WhatIfPreviewsInboundFkDropAndTableDropWithoutApplying()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"InboundFkWiProduct_{uniqueId}";
        var parent = $"InboundFkWiParent_{uniqueId}";
        var child = $"InboundFkWiChild_{uniqueId}";
        var fkName = $"FK_{child}_{parent}";

        var messages = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithParentAndFk(parent, child, fkName), productName: product);

            messages.Clear();
            RunTableQuenchProc(cmd, ChildOnly(child), dropTablesRemovedFromProduct: true, whatIf: true, productName: product);

            var preview = string.Join(" | ", messages);
            Assert.Multiple(() =>
            {
                Assert.That(preview, Does.Contain(fkName),
                    $"WhatIf must preview the inbound FK drop. Preview: {preview}");
                Assert.That(preview, Does.Contain(parent),
                    $"WhatIf must preview the removed-table drop. Preview: {preview}");
                Assert.That(ObjectExists(cmd, parent), Is.True, "WhatIf must NOT drop the table.");
                Assert.That(ForeignKeyExists(cmd, fkName), Is.True, "WhatIf must NOT drop the inbound FK.");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{child}]; DROP TABLE IF EXISTS [dbo].[{parent}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string WithParentAndFk(string parent, string child, string fkName) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{parent}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{parent}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  },
  {
    "Schema": "[dbo]",
    "Name": "[{{child}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT", "Nullable": false },
      { "Name": "[ParentId]", "DataType": "INT", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{child}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
    "ForeignKeys": [
      { "Name": "[{{fkName}}]", "Columns": "[ParentId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[{{parent}}]", "RelatedColumns": "[Id]", "DeleteAction": "NO ACTION", "UpdateAction": "NO ACTION" }
    ]
  }
]
""";

    private static string ChildOnly(string child) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{child}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT", "Nullable": false },
      { "Name": "[ParentId]", "DataType": "INT", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{child}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static bool ObjectExists(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static bool ForeignKeyExists(IDbCommand cmd, string fkName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.foreign_keys WHERE name = '{fkName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }
}
