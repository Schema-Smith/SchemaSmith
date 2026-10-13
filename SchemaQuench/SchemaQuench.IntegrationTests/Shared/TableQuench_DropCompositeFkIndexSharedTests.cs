// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

public abstract class TableQuench_DropCompositeFkIndexSharedTests : BaseTableQuenchTests
{
    // Unique product name: STEP 8's removed-from-product detection is keyed off
    // SchemaSmith_ProductOwnership, so an isolated name keeps a concurrent sibling quench
    // from interfering (same rationale as the single-column FK/index drop test siblings).
    private const string CompositeFkProduct = "Composite FK Index Drop Tests";

    // A composite UNIQUE index a composite foreign key references, removed from the package. The deploy used to drop
    // every foreign key referencing the index's table to make way -- first twice for a composite key (1091), then once --
    // destroying a key the package still declares. No key is dropped now: the engine refuses to drop an index a key needs
    // (1553), so the index is kept and the key with it, and phase 2 succeeds.
    //
    // Two-phase: phase 1 (OneTimeSetUp) deploys CompFkParent with uq_parent_ab and CompFkChild with its composite key
    // against it; phase 2 runs ParseTableJson + ModifiedTableQuench for CompFkParent alone, without uq_parent_ab, and
    // with DropUnknownIndexes and DropIndexesRemovedFromProduct on.
    [Test]
    public void RemovingAUniqueIndexACompositeForeignKeyNeeds_KeepsTheIndexAndTheKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'CompFkChild' AND CONSTRAINT_NAME = 'FK_CompFkChild_Parent_AB' AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "removing an index must never drop a foreign key");

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'CompFkParent' AND INDEX_NAME = 'uq_parent_ab'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0), "the index the key references is kept while the key needs it");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Phase 1 — quench parent (with composite UNIQUE index) and child (with composite FK
        // referencing it) into existence via the full pipeline, so uq_parent_ab becomes
        // product-owned and the composite FK is created against it.
        var withIndex = """
            [
            {
                "Name": "CompFkParent",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "ColA", "DataType": "INT", "Nullable": false },
                    { "Name": "ColB", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "PK_CompFkParent", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" },
                    { "Name": "uq_parent_ab", "PrimaryKey": false, "Unique": true, "IndexColumns": "ColA,ColB" }
                ]
            },
            {
                "Name": "CompFkChild",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "ColA", "DataType": "INT", "Nullable": true },
                    { "Name": "ColB", "DataType": "INT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PK_CompFkChild", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
                ],
                "ForeignKeys": [
                    { "Name": "FK_CompFkChild_Parent_AB", "Columns": "ColA,ColB", "RelatedTable": "CompFkParent", "RelatedColumns": "ColA,ColB" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withIndex, productName: CompositeFkProduct);

        // Phase 2 — reconcile CompFkParent with uq_parent_ab removed from its definition. Run
        // ModifiedTableQuench directly — it owns STEP 8, the index-removal step — with
        // DropUnknownIndexes=1. CompFkChild need not be listed, and DropTablesRemovedFromProduct is 0
        // below, so leaving it out cannot drop it.
        var withoutIndex = """
            [
            {
                "Name": "CompFkParent",
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "ColA", "DataType": "INT", "Nullable": false },
                    { "Name": "ColB", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "PK_CompFkParent", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
                ]
            }
            ]
            """;
        cmd.CommandTimeout = 300;
        // Args: product, db, WhatIf=0, DropTablesRemovedFromProduct=0 (CompFkChild is deliberately
        // absent from the phase-2 definition and must NOT be dropped), DropColumnsRemovedFromProduct=0,
        // DropCheck/Exclude/Statistics=1, CaptureWouldDrop=0, then DropUnknownIndexes=1 and
        // DropIndexesRemovedFromProduct=1 — the flags that make STEP 8 drop uq_parent_ab.
        // MissingIndexesAndConstraintsQuench is not called: phase 2 declares only the PK (already
        // present) and no check constraints, so its create/constraint half has nothing to do.
        cmd.CommandText =
            $"CALL SchemaSmith_ParseTableJson('{_mainDb}', '{withoutIndex.Replace("'", "''")}'); " +
            $"CALL SchemaSmith_ModifiedTableQuench('{CompositeFkProduct}', '{_mainDb}', 0, 0, 0, 1, 1, 1, 0, 1, 1);";
        cmd.ExecuteNonQuery();

        conn.Close();
    }
}
