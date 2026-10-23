// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Regression coverage for #289: dropping a table removed from the product must first drop any
// foreign key that REFERENCES it. A kept table keeps its FK columns, but the product no longer
// declares the FK and no longer declares the referenced table. The removed-table drop ran before
// the FK drop, so the table drop failed while the inbound FK still referenced it.
//
// Each test owns a UNIQUE product name so DropTablesRemovedFromProduct is scoped to its own tables
// and never drops a sibling test's tables under parallel execution.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_RemovedTableInboundFkTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_DropsInboundForeignKeyBeforeDroppingRemovedTable()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "public";
        var product = $"InboundFkProduct_{uniqueId}";
        var parent = $"InboundFkParent_{uniqueId}"; // referenced table, removed in the second quench
        var child = $"InboundFkChild_{uniqueId}";    // kept table that holds the inbound FK
        var fkName = $"FK_{child}_{parent}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Establish product ownership of both tables, with the inbound FK in place.
            RunTableQuenchProc(cmd, WithParentAndFk(schema, parent, child, fkName), productName: product);
            Assert.That(ObjectExists(cmd, schema, parent), Is.True, "Setup: parent table should exist after the first quench.");
            Assert.That(ForeignKeyExists(cmd, schema, fkName), Is.True, "Setup: inbound FK should exist after the first quench.");

            // Remove the parent table AND the child's FK from the product, with autodrop on.
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, ChildOnly(schema, child), dropTablesRemovedFromProduct: true, productName: product),
                "Quench must drop the inbound FK before dropping the removed table (#289).");

            Assert.Multiple(() =>
            {
                Assert.That(ObjectExists(cmd, schema, parent), Is.False, "Removed parent table should be dropped.");
                Assert.That(ForeignKeyExists(cmd, schema, fkName), Is.False, "Inbound FK should be dropped.");
                Assert.That(ObjectExists(cmd, schema, child), Is.True, "Kept child table must survive.");
            });
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{child}""; DROP TABLE IF EXISTS ""{schema}"".""{parent}"";";
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
        var schema = "public";
        var product = $"InboundFkWiProduct_{uniqueId}";
        var parent = $"InboundFkWiParent_{uniqueId}";
        var child = $"InboundFkWiChild_{uniqueId}";
        var fkName = $"FK_{child}_{parent}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithParentAndFk(schema, parent, child, fkName), productName: product);

            messages.Clear();
            RunTableQuenchProc(cmd, ChildOnly(schema, child), dropTablesRemovedFromProduct: true, whatIf: true, productName: product);

            var preview = string.Join(" | ", messages);
            Assert.Multiple(() =>
            {
                Assert.That(preview, Does.Contain(fkName),
                    $"WhatIf must preview the inbound FK drop. Preview: {preview}");
                Assert.That(preview, Does.Contain(parent),
                    $"WhatIf must preview the removed-table drop. Preview: {preview}");
                Assert.That(ObjectExists(cmd, schema, parent), Is.True, "WhatIf must NOT drop the table.");
                Assert.That(ForeignKeyExists(cmd, schema, fkName), Is.True, "WhatIf must NOT drop the inbound FK.");
            });
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{child}""; DROP TABLE IF EXISTS ""{schema}"".""{parent}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string WithParentAndFk(string schema, string parent, string child, string fkName) => $$"""
[
  {
    "Schema": "{{schema}}",
    "Name": "{{parent}}",
    "Columns": [ { "Name": "Id", "DataType": "integer", "Nullable": false } ],
    "Indexes": [ { "Name": "PK_{{parent}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
  },
  {
    "Schema": "{{schema}}",
    "Name": "{{child}}",
    "Columns": [
      { "Name": "Id",       "DataType": "integer", "Nullable": false },
      { "Name": "ParentId", "DataType": "integer", "Nullable": false }
    ],
    "Indexes": [ { "Name": "PK_{{child}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
    "ForeignKeys": [
      { "Name": "{{fkName}}", "Columns": "ParentId", "RelatedTableSchema": "{{schema}}", "RelatedTable": "{{parent}}", "RelatedColumns": "Id", "DeleteAction": "NO ACTION", "UpdateAction": "NO ACTION" }
    ]
  }
]
""";

    private static string ChildOnly(string schema, string child) => $$"""
[
  {
    "Schema": "{{schema}}",
    "Name": "{{child}}",
    "Columns": [
      { "Name": "Id",       "DataType": "integer", "Nullable": false },
      { "Name": "ParentId", "DataType": "integer", "Nullable": false }
    ],
    "Indexes": [ { "Name": "PK_{{child}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
  }
]
""";

    private static bool ObjectExists(IDbCommand cmd, string schema, string tableName)
    {
        cmd.CommandText = $"SELECT to_regclass('\"{schema}\".\"{tableName}\"') IS NOT NULL";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static bool ForeignKeyExists(IDbCommand cmd, string schema, string fkName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_constraint con JOIN pg_namespace n ON n.oid = con.connamespace WHERE con.contype = 'f' AND con.conname = '{fkName}' AND n.nspname = '{schema}'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
