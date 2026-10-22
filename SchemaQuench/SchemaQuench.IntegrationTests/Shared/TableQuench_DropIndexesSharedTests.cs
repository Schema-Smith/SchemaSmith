// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

public abstract class TableQuench_DropIndexesSharedTests : BaseTableQuenchTests
{
    // Unique product name: MySQL records index ownership in the persistent SchemaSmith_ProductOwnership
    // table keyed by product, so an isolated name keeps a concurrent sibling quench from interfering.
    private const string IndexProduct = "Index Drop Tests";

    // Two-phase: an index must be product-OWNED (recorded in ProductOwnership) to take the
    // removed-from-product path. Phase 1 quenches the index into existence; phase 2 reconciles with
    // the index removed. Index removal (STEP 8) lives in SchemaSmith_ModifiedTableQuench, so phase 2
    // runs THAT procedure with DropUnknownIndexes=1 / DropIndexesRemovedFromProduct=1; the per-table
    // DropIndexesRemovedFromProduct:false then suppresses IdxDropSuppressed while IdxDropControl
    // (no flag) drops.
    [Test]
    public void TableQuench_ShouldSuppressIndexDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'IdxDropSuppressed' AND INDEX_NAME = 'IX_IdxDropSuppressed_Val'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThan(0), "IX_IdxDropSuppressed_Val should still exist (suppressed by table flag).");

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'IdxDropControl' AND INDEX_NAME = 'IX_IdxDropControl_Val'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "IX_IdxDropControl_Val should be gone (dropped by absence).");

        conn.Close();
    }

    // MySQL creates an index for a foreign key that has none declared, so that index is "unknown" to the package. With
    // DropUnknownIndexes on, every redeploy tried to drop it and failed with 1553, because the key still needs it. It is
    // kept while the key exists; once the key is removed, the next deploy drops it.
    [Test]
    public void DropUnknownIndexes_KeepsTheIndexAForeignKeyNeeds_ThenDropsItOnceTheKeyIsGone()
    {
        const string product = "Fk Needed Index Tests";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        string Tables(bool withForeignKey) => $$"""
            [{ "Name": "`FkNeedParent`", "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false } ],
               "Indexes": [ { "Name": "`PRIMARY`", "IndexColumns": "`Id`", "Unique": true, "PrimaryKey": true } ] },
             { "Name": "`FkNeedChild`", "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`ParentId`", "DataType": "INT", "Nullable": true } ],
               "Indexes": [ { "Name": "`PRIMARY`", "IndexColumns": "`Id`", "Unique": true, "PrimaryKey": true } ],
               "ForeignKeys": [ {{(withForeignKey ? "{ \"Name\": \"`FK_FkNeedChild_Parent`\", \"Columns\": \"`ParentId`\", \"RelatedTable\": \"`FkNeedParent`\", \"RelatedColumns\": \"`Id`\" }" : "")}} ] }]
            """;
        void Deploy(bool withForeignKey)
        {
            cmd.CommandText = $"CALL SchemaSmith_TableQuench('{product}', '{_mainDb}', '{Tables(withForeignKey).Replace("'", "''")}', 0, 1, 0);";
            RetryingTransientConcurrency(() => cmd.ExecuteNonQuery());
        }
        long Count(string sql)
        {
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        var fkIndex = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'FkNeedChild' AND INDEX_NAME = 'FK_FkNeedChild_Parent'";

        try
        {
            Deploy(withForeignKey: true);
            Assert.DoesNotThrow(() => Deploy(withForeignKey: true), "an unchanged redeploy must not try to drop the foreign key's index");
            Assert.That(Count(fkIndex), Is.EqualTo(1), "the index stays while the key needs it");

            Assert.DoesNotThrow(() => Deploy(withForeignKey: false), "removing the key must not fail on its index");
            Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'FkNeedChild' AND CONSTRAINT_TYPE = 'FOREIGN KEY'"), Is.Zero);

            Deploy(withForeignKey: false);
            Assert.That(Count(fkIndex), Is.Zero, "with the key gone, the unknown index is dropped");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`FkNeedChild`, `{_mainDb}`.`FkNeedParent`; " +
                              $"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = '{product}';";
            cmd.ExecuteNonQuery();
        }
    }

    // Before dropping an unknown unique index, the deploy dropped every foreign key that referenced the index's TABLE --
    // keys on another column included -- and re-created only the ones the package declares. A key outside the package
    // was lost. No key is dropped now: an index a key references is kept, and the other unknown index still goes.
    [Test]
    public void DropUnknownIndexes_NeverDropsAForeignKeyThePackageDoesNotDeclare()
    {
        const string product = "Fk Predrop Tests";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString();
        }

        try
        {
            cmd.CommandText = $"""
                CREATE TABLE `{_mainDb}`.`FkPreParent` (`Id` INT NOT NULL PRIMARY KEY, `Code` INT NOT NULL, `Other` INT NOT NULL,
                  UNIQUE KEY `UQ_Code` (`Code`), UNIQUE KEY `UQ_Other` (`Other`));
                CREATE TABLE `{_mainDb}`.`FkPreByCode` (`Id` INT NOT NULL PRIMARY KEY, `ParentCode` INT NOT NULL,
                  CONSTRAINT `FK_FkPreByCode` FOREIGN KEY (`ParentCode`) REFERENCES `FkPreParent` (`Code`));
                CREATE TABLE `{_mainDb}`.`FkPreById` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NOT NULL,
                  CONSTRAINT `FK_FkPreById` FOREIGN KEY (`ParentId`) REFERENCES `FkPreParent` (`Id`));
                """;
            cmd.ExecuteNonQuery();
            const string parentOnly = """
                [{ "Name": "`FkPreParent`",
                   "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Code`", "DataType": "INT", "Nullable": false },
                                { "Name": "`Other`", "DataType": "INT", "Nullable": false } ],
                   "Indexes": [ { "Name": "`PRIMARY`", "IndexColumns": "`Id`", "Unique": true, "PrimaryKey": true } ] }]
                """;
            cmd.CommandText = $"CALL SchemaSmith_TableQuench('{product}', '{_mainDb}', '{parentOnly.Replace("'", "''")}', 0, 1, 0);";
            Assert.DoesNotThrow(() => RetryingTransientConcurrency(() => cmd.ExecuteNonQuery()));

            Assert.Multiple(() =>
            {
                Assert.That(Scalar($"SELECT GROUP_CONCAT(CONSTRAINT_NAME ORDER BY CONSTRAINT_NAME) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS " +
                                   $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME IN ('FkPreByCode', 'FkPreById') AND CONSTRAINT_TYPE = 'FOREIGN KEY'"),
                    Is.EqualTo("FK_FkPreByCode,FK_FkPreById"), "keys outside the package are not the deploy's to drop");
                Assert.That(Scalar($"SELECT GROUP_CONCAT(DISTINCT INDEX_NAME ORDER BY INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS " +
                                   $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'FkPreParent'"), Is.EqualTo("PRIMARY,UQ_Code"),
                    "the index a key references is kept; the unreferenced unknown index is dropped");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`FkPreByCode`, `{_mainDb}`.`FkPreById`, `{_mainDb}`.`FkPreParent`; " +
                              $"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = '{product}';";
            cmd.ExecuteNonQuery();
        }
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Phase 1 — quench the tables WITH their secondary index (creates + records ownership).
        var withIndex = """
            [
            {
                "Name": "`IdxDropSuppressed`",
                "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "`IX_IdxDropSuppressed_Val`", "IndexColumns": "`Val`", "Unique": false, "PrimaryKey": false } ]
            },
            {
                "Name": "`IdxDropControl`",
                "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "`IX_IdxDropControl_Val`", "IndexColumns": "`Val`", "Unique": false, "PrimaryKey": false } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withIndex, productName: IndexProduct);

        // Phase 2 — reconcile with the secondary index removed from both. IdxDropSuppressed protects
        // its own via the per-table flag. Run ModifiedTableQuench directly — that is the procedure
        // that performs index removal — with DropUnknownIndexes=1 and DropIndexesRemovedFromProduct=1
        // so STEP 8 fires; the per-table flag gates suppression.
        var withoutIndex = """
            [
            {
                "Name": "`IdxDropSuppressed`",
                "DropIndexesRemovedFromProduct": false,
                "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
                "Indexes": []
            },
            {
                "Name": "`IdxDropControl`",
                "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
                "Indexes": []
            }
            ]
            """;
        cmd.CommandTimeout = 300;
        // Args: product, db, WhatIf=0, DropTablesRemovedFromProduct=0, DropColumnsRemovedFromProduct=0
        // (both tables and both columns are still declared, and this test must not start dropping
        // either), DropCheck/Exclude/Statistics=1, CaptureWouldDrop=0, then the two index-drop flags
        // this phase actually exercises: DropUnknownIndexes=1, DropIndexesRemovedFromProduct=1.
        // MissingIndexesAndConstraintsQuench is not called: the phase-2 definition declares no indexes
        // and no check constraints, so its create/constraint half has nothing to do here.
        cmd.CommandText =
            $"CALL SchemaSmith_ParseTableJson('{_mainDb}', '{withoutIndex.Replace("'", "''")}'); " +
            $"CALL SchemaSmith_ModifiedTableQuench('{IndexProduct}', '{_mainDb}', 0, 0, 0, 1, 1, 1, 0, 1, 1);";
        cmd.ExecuteNonQuery();

        conn.Close();
    }
}
