// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

public abstract class TableQuench_DropForeignKeysSharedTests : BaseTableQuenchTests
{
    // A dedicated product name so concurrent MySQL tests (which share the default product name)
    // do not reconcile this test's product-owned FKs: MySQL STEP 4 drops ALL FKs owned by a
    // product that are absent from the CURRENT quench's definition, so a sibling quench under
    // the shared product name would otherwise drop FK_FkDropSuppressed_Ref out from under us.
    private const string FkProductName = "FK Drop Tests";

    // The FKs are deployed through the product (so they are owned), then removed in a second
    // quench. RunTableQuenchProc passes DropUnknownIndexes = 0, so FkDropControl's FK dropping
    // proves the D-MYSQL-FK decouple: MySQL now cleans up orphaned foreign keys by absence
    // WITHOUT requiring DropUnknownIndexes (the ### Fixed). Non-vacuous: FkDropSuppressed sets
    // DropForeignKeysRemovedFromProduct:false and its FK survives in the same quench.
    [Test]
    public void TableQuench_ShouldSuppressForeignKeyDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND CONSTRAINT_NAME = 'FK_FkDropSuppressed_Ref' AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "FK_FkDropSuppressed_Ref should still exist (suppressed by table flag).");

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND CONSTRAINT_NAME = 'FK_FkDropControl_Ref' AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "FK_FkDropControl_Ref should be gone (dropped by absence with DropUnknownIndexes=0).");

        conn.Close();
    }

    // MySQL backs a key with an index of its own name when no index leads with its columns, and a later key on the
    // leading columns uses that index rather than making one. A key the package drops or changes can leave its index
    // holding up a key it does not own; the deploy keeps that index rather than failing.
    [Test]
    public void AKeyTheProductDrops_WhoseIndexAnotherKeyUses_IsDropped_AndTheDeploySucceeds()
    {
        DeployOverAKeyThatSharesItsIndex(_ => "", (uid, keys, _) =>
            Assert.That(keys($"fk_owned_{uid}"), Is.EqualTo(0), "the key the package dropped is gone"));
    }

    [Test]
    public void AKeyTheProductChanges_WhoseIndexAnotherKeyUses_IsChanged_AndTheDeploySucceeds()
    {
        DeployOverAKeyThatSharesItsIndex(uid => OwnedKey(uid, "CASCADE"), (uid, keys, scalar) =>
        {
            Assert.That(keys($"fk_owned_{uid}"), Is.EqualTo(1), "the key the package changed is there");
            Assert.That(scalar("SELECT DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                               + $"WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND CONSTRAINT_NAME = 'fk_owned_{uid}'"),
                Is.EqualTo("CASCADE"), "with the change the package made");
        });
    }

    private static string OwnedKey(string uid, string deleteAction) =>
        $$""", "ForeignKeys": [ { "Name": "fk_owned_{{uid}}", "Columns": "RefId,RefId2", "RelatedTable": "si_parent_{{uid}}", "RelatedColumns": "Id,Id2", "DeleteAction": "{{deleteAction}}" } ]""";

    private void DeployOverAKeyThatSharesItsIndex(Func<string, string> secondKeys,
        Action<string, Func<string, int>, Func<string, string>> assertAfter)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"Shared index {uid}";
        string Json(string keys) => $$"""
            [
            { "Name": "si_parent_{{uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Id2", "DataType": "INT", "Nullable": false } ],
              "Indexes": [ { "Name": "PK_si_parent_{{uid}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id,Id2" },
                           { "Name": "UQ_si_parent_{{uid}}", "Unique": true, "IndexColumns": "Id" } ] },
            { "Name": "si_child_{{uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true }, { "Name": "RefId2", "DataType": "INT", "Nullable": true } ],
              "Indexes": [ { "Name": "PK_si_child_{{uid}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]{{keys}} }
            ]
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        int Keys(string name) => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
            + $"WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND CONSTRAINT_NAME = '{name}' AND CONSTRAINT_TYPE = 'FOREIGN KEY'"));
        try
        {
            RunTableQuenchProc(cmd, Json(OwnedKey(uid, "RESTRICT")), productName: product);
            Scalar($"ALTER TABLE `si_child_{uid}` ADD CONSTRAINT `fk_hand_{uid}` FOREIGN KEY (`RefId`) REFERENCES `si_parent_{uid}` (`Id`)");
            Assert.That(Scalar("SELECT GROUP_CONCAT(DISTINCT INDEX_NAME ORDER BY INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                               + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'si_child_{uid}'"),
                Is.EqualTo($"fk_owned_{uid},PRIMARY"), "the key added by hand uses the packaged key's index rather than one of its own");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(secondKeys(uid)), productName: product));

            Assert.Multiple(() =>
            {
                assertAfter(uid, Keys, Scalar);
                Assert.That(Keys($"fk_hand_{uid}"), Is.EqualTo(1), "and the key it does not own is untouched");
            });
        }
        finally
        {
            Scalar($"DROP TABLE IF EXISTS `si_child_{uid}`, `si_parent_{uid}`");
            TransientRetry.Run(() => Scalar($"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = '{product}'"));
        }
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Quench 1: deploy the tables WITH their foreign keys so they become product-owned.
        var withFks = """
            [
            {
                "Name": "FkDropRef",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PK_FkDropRef", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Name": "FkDropSuppressed",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FkDropSuppressed", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
                "ForeignKeys": [ { "Name": "FK_FkDropSuppressed_Ref", "Columns": "RefId", "RelatedTable": "FkDropRef", "RelatedColumns": "Id" } ]
            },
            {
                "Name": "FkDropControl",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FkDropControl", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
                "ForeignKeys": [ { "Name": "FK_FkDropControl_Ref", "Columns": "RefId", "RelatedTable": "FkDropRef", "RelatedColumns": "Id" } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withFks, productName: FkProductName);

        // Quench 2: remove the foreign keys. FkDropSuppressed protects its own FK with the flag.
        var withoutFks = """
            [
            {
                "Name": "FkDropRef",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PK_FkDropRef", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Name": "FkDropSuppressed",
                "DropForeignKeysRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FkDropSuppressed", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Name": "FkDropControl",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FkDropControl", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, withoutFks, productName: FkProductName);

        conn.Close();
    }
}
