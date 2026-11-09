// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using System.Data;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

// A table rebuild drops every foreign key that references the table, because MySQL moves an inbound key to follow
// the table renamed aside. Only keys this package declares came back, so a key another product holds, or one a DBA
// added, was gone for good. A rebuild now sets every inbound key aside and the foreign-key pass restores it as it was.
public abstract class RebuildTablePreservationSharedTests : BaseTableQuenchTests
{
    private sealed class Run : IDisposable
    {
        public readonly IDbConnection Conn;
        public readonly string Uid = Guid.NewGuid().ToString("N")[..8];
        public readonly string Db;
        public string Parent => $"rbp_{Uid}";
        public string Child => $"rbc_{Uid}";
        public string Other => $"rbo_{Uid}";
        // MySQL names a foreign key once per database, and these fixtures run side by side in one.
        public string ChildKey => $"fk_child_{Uid}";
        public string OtherDb => $"rbx_{Uid}";

        public Run(Platform platform, string connectionString, string db)
        {
            Db = db;
            Conn = DbConnectionFactory.ForPlatform(platform).GetDbConnection(connectionString);
            Conn.Open();
            Exec($"USE `{Db}`");
        }

        public void Exec(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public string? Scalar(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? null : v.ToString();
        }

        public string Json(string valType, string idType = "INT", bool child = false, string? childKey = null,
                           bool primaryKey = true, bool uniqueCode = false)
        {
            var indexes = (primaryKey ? """{ "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }""" : "")
                          + (primaryKey && uniqueCode ? ", " : "")
                          + (uniqueCode ? """{ "Name": "uq_code", "Unique": true, "IndexColumns": "Code" }""" : "");
            return $$"""
[ { "Name": "{{Parent}}", "RebuildPolicy": { "Mode": "ALWAYS" },
    "Columns": [ { "Name": "Id", "DataType": "{{idType}}", "Nullable": false }, { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": true },
                 { "Name": "Val", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ {{indexes}} ] }
""" + (child ? $$"""
, { "Name": "{{Child}}",
    "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "ParentId", "DataType": "INT", "Nullable": true } ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
    "ForeignKeys": [ { "Name": "{{childKey ?? ChildKey}}", "Columns": "ParentId", "RelatedTable": "{{Parent}}", "RelatedColumns": "Id" } ] }
""" : "") + "]";
        }

        // The key's delete rule when it is on its table and references the live parent; null when it is not.
        public string? Key(string table, string name, string? db = null) => Scalar(
            "SELECT DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS " +
            $"WHERE CONSTRAINT_SCHEMA = '{db ?? Db}' AND TABLE_NAME = '{table}' AND CONSTRAINT_NAME = '{name}' " +
            $"AND UNIQUE_CONSTRAINT_SCHEMA = '{Db}' AND REFERENCED_TABLE_NAME = '{Parent}'");

        public string SetAside(string table) => Scalar(
            $"SELECT COUNT(*) FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE ChildTable = '{table}'") ?? "";

        public string? CreateTime() => Scalar(
            $"SELECT CREATE_TIME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{Db}' AND TABLE_NAME = '{Parent}'");

        public string Log() => Scalar("SELECT GROUP_CONCAT(Message SEPARATOR ' | ') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()") ?? "";

        public void Dispose()
        {
            Exec($"DROP DATABASE IF EXISTS `{OtherDb}`");
            Exec($"DROP TABLE IF EXISTS `{Other}`, `{Child}`, `unrel_{Uid}`");
            Exec($"DROP TABLE IF EXISTS `{Parent}`, `{Parent}_SchemaSmithRebuild`, `{Parent}_SchemaSmithOld`");
            Exec($"DELETE FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE ChildTable IN ('{Other}', '{Child}', '{Parent}')");
            Conn.Dispose();
        }
    }

    private void Deploy(Run r, string json, string productName = "") => RunTableQuenchProc(r.Conn.CreateCommand(), json, productName: productName);

    private Run Seeded(bool uniqueCode = false)
    {
        var r = new Run(Platform, _connectionString, _mainDb);
        Deploy(r, r.Json("VARCHAR(50)", uniqueCode: uniqueCode));
        r.Exec($"INSERT INTO `{r.Parent}` (`Id`, `Code`, `Val`) VALUES (1, 'a', 'a'), (2, 'b', 'b')");
        r.Exec("SET SESSION group_concat_max_len = 1000000");
        return r;
    }

    // CREATE_TIME has one-second resolution, so a rebuild in the same second as the seed would not show.
    private static void NextSecond() => System.Threading.Thread.Sleep(1100);

    [Test]
    public void AKeyTheProductDoesNotDeclare_SurvivesARebuildOfTheTableItReferences()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NULL,
                    CONSTRAINT `fk_other_{r.Uid}` FOREIGN KEY (`ParentId`) REFERENCES `{r.Parent}` (`Id`) ON DELETE CASCADE);
                  INSERT INTO `{r.Other}` VALUES (10, 1);");
        var before = r.CreateTime();
        NextSecond();

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.CreateTime(), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Key(r.Other, $"fk_other_{r.Uid}"), Is.EqualTo("CASCADE"), "the key is back, on the new table, with its actions");
            Assert.That(() => r.Exec($"INSERT INTO `{r.Other}` VALUES (11, 99)"),
                Throws.InstanceOf<MySqlException>().With.Property("Number").EqualTo(1452), "and enforcing");
            Assert.That(r.SetAside(r.Other), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AKeyFromAnotherDatabase_SurvivesARebuildOfTheTableItReferences()
    {
        using var r = Seeded();
        r.Exec($@"CREATE DATABASE `{r.OtherDb}`;
                  CREATE TABLE `{r.OtherDb}`.`c` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NULL,
                    CONSTRAINT `fk_xdb_{r.Uid}` FOREIGN KEY (`ParentId`) REFERENCES `{r.Db}`.`{r.Parent}` (`Id`));");

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.That(r.Key("c", $"fk_xdb_{r.Uid}", r.OtherDb), Is.Not.Null);
    }

    [Test]
    public void AKeyCreatedOverOrphanRows_ComesBackAsItWas()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NULL);
                  INSERT INTO `{r.Other}` VALUES (10, 99);
                  SET foreign_key_checks = 0;
                  ALTER TABLE `{r.Other}` ADD CONSTRAINT `fk_orphan_{r.Uid}` FOREIGN KEY (`ParentId`) REFERENCES `{r.Parent}` (`Id`);
                  SET foreign_key_checks = 1;");

        Assert.DoesNotThrow(() => Deploy(r, r.Json("VARCHAR(60)")), "re-added with checks on, it failed on the orphan row");
        Assert.That(r.Key(r.Other, $"fk_orphan_{r.Uid}"), Is.Not.Null);
    }

    [Test]
    public void AnUndeclaredKeyOverAColumnThatChangesType_RefusesTheRebuildByName_BeforeAnythingChanges()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NULL,
                    CONSTRAINT `fk_other_{r.Uid}` FOREIGN KEY (`ParentId`) REFERENCES `{r.Parent}` (`Id`));");
        var before = r.CreateTime();
        NextSecond();

        var ex = Assert.Catch(() => Deploy(r, r.Json("VARCHAR(60)", idType: "BIGINT")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"fk_other_{r.Uid}"));
            Assert.That(r.Log(), Does.Contain($"fk_other_{r.Uid}").And.Contain("the referenced columns to keep their current definition"));
            Assert.That(r.CreateTime(), Is.EqualTo(before), "the table was not rebuilt");
            Assert.That(r.Key(r.Other, $"fk_other_{r.Uid}"), Is.Not.Null, "and the key is where it was");
        });
    }

    [Test]
    public void AnUndeclaredKeyWithNoDeclaredKeyToReference_RefusesTheRebuildByName()
    {
        using var r = Seeded();
        r.Exec($@"ALTER TABLE `{r.Parent}` ADD UNIQUE KEY `uq_dba` (`Code`);
                  CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `Code` VARCHAR(20) NULL,
                    CONSTRAINT `fk_code_{r.Uid}` FOREIGN KEY (`Code`) REFERENCES `{r.Parent}` (`Code`));");

        var ex = Assert.Catch(() => Deploy(r, r.Json("VARCHAR(60)")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"fk_code_{r.Uid}"));
            Assert.That(r.Log(), Does.Contain("a declared primary key or unique index over exactly (`Code`)"));
            Assert.That(r.Key(r.Other, $"fk_code_{r.Uid}"), Is.Not.Null);
        });
    }

    [Test]
    public void AnUndeclaredKeyOverAColumnTheRebuildDrops_RefusesTheRebuildByName()
    {
        using var r = Seeded(uniqueCode: true);
        r.Exec($@"CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `Code` VARCHAR(20) NULL,
                    CONSTRAINT `fk_code_{r.Uid}` FOREIGN KEY (`Code`) REFERENCES `{r.Parent}` (`Code`));");
        var withoutCode = $$"""
[ { "Name": "{{r.Parent}}", "RebuildPolicy": { "Mode": "ALWAYS" },
    "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Val", "DataType": "VARCHAR(60)", "Nullable": true } ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] } ]
""";

        var ex = Assert.Catch(() => Deploy(r, withoutCode));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"fk_code_{r.Uid}"));
            Assert.That(r.Log(), Does.Contain("every referenced column to stay declared"));
            Assert.That(r.Key(r.Other, $"fk_code_{r.Uid}"), Is.Not.Null);
        });
    }

    [Test]
    public void AnUndeclaredKeyOnTheProductsOwnTable_IsKept_AsTheInPlacePathKeepsIt()
    {
        using var r = Seeded();
        Deploy(r, r.Json("VARCHAR(50)", child: true));
        r.Exec($"ALTER TABLE `{r.Child}` ADD CONSTRAINT `fk_extra_{r.Uid}` FOREIGN KEY (`ParentId`) REFERENCES `{r.Parent}` (`Id`)");

        Deploy(r, r.Json("VARCHAR(60)", child: true));

        Assert.Multiple(() =>
        {
            Assert.That(r.Key(r.Child, r.ChildKey), Is.Not.Null, "the declared key is back");
            Assert.That(r.Key(r.Child, $"fk_extra_{r.Uid}"), Is.Not.Null,
                "a key this product never declared is not its to drop, rebuild or not");
            Assert.That(r.SetAside(r.Child), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AKeyThisProductRemovedFromItsPackage_IsDropped_AsTheInPlacePathDropsIt()
    {
        using var r = Seeded();
        Deploy(r, r.Json("VARCHAR(50)", child: true, childKey: $"fk_gone_{r.Uid}"));
        Assert.That(r.Key(r.Child, $"fk_gone_{r.Uid}"), Is.Not.Null, "setup: the product declared and owns the key");

        Deploy(r, r.Json("VARCHAR(60)", child: true, childKey: r.ChildKey));

        Assert.Multiple(() =>
        {
            Assert.That(r.Key(r.Child, r.ChildKey), Is.Not.Null);
            Assert.That(r.Key(r.Child, $"fk_gone_{r.Uid}"), Is.Null, "the key the product removed stays removed");
            Assert.That(r.SetAside(r.Child), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AKeySetAsideByARunThatStoppedBeforeTheForeignKeyPass_IsRestoredByTheNextRun()
    {
        // The shadow carries the declared primary key, so the key that waits references a declared unique index,
        // which only the index pass puts back.
        using var r = Seeded(uniqueCode: true);
        r.Exec($@"CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `Code` VARCHAR(20) NULL,
                    CONSTRAINT `fk_code_{r.Uid}` FOREIGN KEY (`Code`) REFERENCES `{r.Parent}` (`Code`));");
        var esc = r.Json("VARCHAR(60)", uniqueCode: true).Replace("'", "''");
        r.Exec($"CALL SchemaSmith_ParseTableJson('{r.Db}', '{esc}')");
        r.Exec($"CALL SchemaSmith_RebuildTable('{r.Db}', '{r.Parent}', 0)");
        Assert.That(r.Key(r.Other, $"fk_code_{r.Uid}"), Is.Null, "setup: the run stopped with the key set aside");

        // Another product deploys before this one runs again: the rebuilt table has no index for the key yet, so the
        // key cannot come back -- and that must not fail a deploy that has nothing to do with it.
        Assert.DoesNotThrow(() => Deploy(r, $$"""
[ { "Name": "unrel_{{r.Uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ] } ]
""", productName: $"Unrelated_{r.Uid}"));
        Assert.That(r.SetAside(r.Other), Is.EqualTo("1"), "and the key waits");

        Deploy(r, r.Json("VARCHAR(60)", uniqueCode: true));

        Assert.Multiple(() =>
        {
            Assert.That(r.Key(r.Other, $"fk_code_{r.Uid}"), Is.Not.Null);
            Assert.That(r.SetAside(r.Other), Is.EqualTo("0"));
        });
    }

    // MySQL and MariaDB DDL is not transactional, so another deploy's foreign-key pass can run while a rebuild sits between
    // recording its keys and dropping them. That pass saw each key still on its table, cleared the record as done with,
    // and the rebuild then dropped the key with nothing left to restore it. The rebuild holds this lock for that span.
    [Test]
    public void AKeyStillOnItsTableMidRebuild_IsLeftRecorded_UntilTheRebuildFinishes()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE `{r.Other}` (`Id` INT NOT NULL PRIMARY KEY, `ParentId` INT NULL,
                    CONSTRAINT `fk_mid_{r.Uid}` FOREIGN KEY (`ParentId`) REFERENCES `{r.Parent}` (`Id`));");
        r.Exec($@"INSERT INTO SchemaSmith_RebuildSetAsideForeignKeys (ChildSchema, ChildTable, KeyName, ParentSchema, ParentTable, ReferencedColumns, Definition, Declared)
                  VALUES ('{r.Db}', '{r.Other}', 'fk_mid_{r.Uid}', '{r.Db}', '{r.Parent}', '`Id`',
                          'FOREIGN KEY (`ParentId`) REFERENCES `{r.Parent}` (`Id`)', 0)");

        using var rebuilding = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        rebuilding.Open();
        using var lockCmd = rebuilding.CreateCommand();
        lockCmd.CommandText = $"SELECT GET_LOCK(CONCAT('ss_rbfk_', LEFT(SHA2(CONCAT('{r.Db}', '.', '{r.Parent}'), 256), 32)), 5)";
        Assert.That(Convert.ToInt32(lockCmd.ExecuteScalar()), Is.EqualTo(1), "setup: the rebuild's lock is held");

        r.Exec("DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()");
        Deploy(r, $$"""
[ { "Name": "unrel_{{r.Uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ] } ]
""", productName: $"Unrelated_{r.Uid}");
        Assert.Multiple(() =>
        {
            Assert.That(r.SetAside(r.Other), Is.EqualTo("1"), "the record waits for the rebuild");
            Assert.That(r.Log(), Does.Contain("another deploy is still rebuilding"));
        });

        lockCmd.CommandText = $"SELECT RELEASE_LOCK(CONCAT('ss_rbfk_', LEFT(SHA2(CONCAT('{r.Db}', '.', '{r.Parent}'), 256), 32)))";
        lockCmd.ExecuteScalar();
        Deploy(r, $$"""
[ { "Name": "unrel_{{r.Uid}}", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ] } ]
""", productName: $"Unrelated_{r.Uid}");
        Assert.Multiple(() =>
        {
            Assert.That(r.SetAside(r.Other), Is.EqualTo("0"), "once it has finished, a key on its table is done with");
            Assert.That(r.Key(r.Other, $"fk_mid_{r.Uid}"), Is.Not.Null);
        });
    }
}
