// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A table rebuild drops every foreign key that references the table, because an inbound key follows the table renamed
// aside. Only keys this package declares came back, so a key another product holds, or one a DBA added, was gone for
// good, and a declared key that was NOT VALID came back validated -- or failed on the orphan rows it had been created
// over. A rebuild now sets every inbound key aside and the foreign-key pass restores it as it was.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class RebuildTablePreservationTests : BaseTableQuenchTests
{
    private sealed class Run : IDisposable
    {
        public readonly IDbConnection Conn;
        public readonly string Schema = $"rbk_{Guid.NewGuid().ToString("N")[..8]}";
        public string Other => Schema + "_x";

        public Run(string connectionString, string db)
        {
            Conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(connectionString);
            Conn.Open();
            Conn.ChangeDatabase(db);
            Exec($@"CREATE SCHEMA ""{Schema}""; CREATE SCHEMA ""{Other}"";");
        }

        public void Exec(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public string Scalar(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? null : v.ToString();
        }

        public string Json(string valType, string idType = "INT", bool child = false, string childKey = "fk_child") => $$"""
[ { "Schema": "{{Schema}}", "Name": "Parent", "RebuildPolicy": { "Mode": "ALWAYS" },
    "Columns": [ { "Name": "Id", "DataType": "{{idType}}", "Nullable": false }, { "Name": "Val", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ { "Name": "pk_parent", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] }
""" + (child ? $$"""
, { "Schema": "{{Schema}}", "Name": "Child",
    "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "ParentId", "DataType": "INT", "Nullable": true } ],
    "ForeignKeys": [ { "Name": "{{childKey}}", "Columns": "ParentId", "RelatedTableSchema": "{{Schema}}", "RelatedTable": "Parent", "RelatedColumns": "Id" } ] }
""" : "") + "]";

        // On the parent now, validated?
        public string Key(string name) => Scalar(
            $"SELECT CASE WHEN con.confrelid = '\"{Schema}\".\"Parent\"'::regclass THEN '1' ELSE '0' END || '|' || CASE WHEN con.convalidated THEN '1' ELSE '0' END " +
            $"FROM pg_constraint con WHERE con.contype = 'f' AND con.conname = '{name}'");

        public void Dispose()
        {
            Exec($@"DROP SCHEMA IF EXISTS ""{Other}"" CASCADE; DROP SCHEMA IF EXISTS ""{Schema}"" CASCADE;
                    DELETE FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" IN ('{Schema}', '{Other}');");
            Conn.Dispose();
        }
    }

    private void Deploy(Run r, string json) => RunTableQuenchProc(r.Conn.CreateCommand(), json);

    private Run Seeded()
    {
        var r = new Run(_connectionString, _mainDb);
        Deploy(r, r.Json("VARCHAR(50)"));
        r.Exec($@"INSERT INTO ""{r.Schema}"".""Parent"" (""Id"", ""Val"") VALUES (1, 'a'), (2, 'b');");
        return r;
    }

    [Test]
    public void AKeyTheProductDoesNotDeclare_SurvivesARebuildOfTheTableItReferences()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE ""{r.Other}"".""C"" (""Id"" INT NOT NULL, ""ParentId"" INT NULL
                    CONSTRAINT fk_other REFERENCES ""{r.Schema}"".""Parent"" (""Id"") ON DELETE CASCADE);
                  INSERT INTO ""{r.Other}"".""C"" VALUES (10, 1);");
        var before = r.Scalar($"SELECT '\"{r.Schema}\".\"Parent\"'::regclass::oid");

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Scalar($"SELECT '\"{r.Schema}\".\"Parent\"'::regclass::oid"), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Key("fk_other"), Is.EqualTo("1|1"), "the key is back, on the new table, validated");
            Assert.That(r.Scalar("SELECT confdeltype FROM pg_constraint WHERE conname = 'fk_other'"), Is.EqualTo("c"), "with its actions");
            Assert.That(() => r.Exec($@"INSERT INTO ""{r.Other}"".""C"" VALUES (11, 99);"),
                Throws.InstanceOf<PostgresException>().With.Property("SqlState").EqualTo("23503"), "and enforcing");
            Assert.That(r.Scalar($@"SELECT COUNT(*) FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" = '{r.Other}'"), Is.EqualTo("0"));
        });
    }

    [Test]
    public void ANotValidKey_ComesBackNotValid()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE ""{r.Other}"".""C"" (""Id"" INT NOT NULL, ""ParentId"" INT NULL);
                  INSERT INTO ""{r.Other}"".""C"" VALUES (10, 99);
                  ALTER TABLE ""{r.Other}"".""C"" ADD CONSTRAINT fk_nv FOREIGN KEY (""ParentId"") REFERENCES ""{r.Schema}"".""Parent"" (""Id"") NOT VALID;");

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.That(r.Key("fk_nv"), Is.EqualTo("1|0"), "the key created over an orphan row comes back NOT VALID");
    }

    [Test]
    public void ADeclaredKeyLeftNotValid_IsRestoredNotValid_OverTheOrphanRowsItHad()
    {
        using var r = Seeded();
        Deploy(r, r.Json("VARCHAR(50)", child: true));
        r.Exec($@"ALTER TABLE ""{r.Schema}"".""Child"" DROP CONSTRAINT fk_child;
                  INSERT INTO ""{r.Schema}"".""Child"" VALUES (10, 99);
                  ALTER TABLE ""{r.Schema}"".""Child"" ADD CONSTRAINT fk_child FOREIGN KEY (""ParentId"") REFERENCES ""{r.Schema}"".""Parent"" (""Id"") NOT VALID;");

        Assert.DoesNotThrow(() => Deploy(r, r.Json("VARCHAR(60)", child: true)),
            "a declared key that omits Validated keeps what it had; re-added validated it failed on the orphan row");
        Assert.That(r.Key("fk_child"), Is.EqualTo("1|0"));
    }

    [Test]
    public void AnUndeclaredKeyThatCouldNotBeRestored_RefusesTheRebuildByName_BeforeAnythingChanges()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE ""{r.Other}"".""C"" (""Id"" INT NOT NULL, ""ParentId"" INT NULL CONSTRAINT fk_other REFERENCES ""{r.Schema}"".""Parent"" (""Id""));");
        var before = r.Scalar($"SELECT '\"{r.Schema}\".\"Parent\"'::regclass::oid");

        var ex = Assert.Catch(() => Deploy(r, r.Json("VARCHAR(60)", idType: "BIGINT")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("fk_other").And.Contain("the referenced columns to keep their current definition"));
            Assert.That(r.Scalar($"SELECT '\"{r.Schema}\".\"Parent\"'::regclass::oid"), Is.EqualTo(before), "the table was not rebuilt");
            Assert.That(r.Key("fk_other"), Is.EqualTo("1|1"), "and the key is where it was");
        });
    }

    [Test]
    public void AnUndeclaredKeyOnTheProductsOwnTable_IsDroppedAsTheInPlacePathWouldHaveDroppedIt()
    {
        using var r = Seeded();
        Deploy(r, r.Json("VARCHAR(50)", child: true));
        r.Exec($@"ALTER TABLE ""{r.Schema}"".""Child"" ADD CONSTRAINT fk_extra FOREIGN KEY (""ParentId"") REFERENCES ""{r.Schema}"".""Parent"" (""Id"");");

        Deploy(r, r.Json("VARCHAR(60)", child: true));

        Assert.Multiple(() =>
        {
            Assert.That(r.Key("fk_child"), Is.EqualTo("1|1"), "the declared key is back");
            Assert.That(r.Scalar("SELECT COUNT(*) FROM pg_constraint WHERE conname = 'fk_extra'"), Is.EqualTo("0"),
                "the undeclared one on the product's own table is dropped, as it would be without a rebuild");
            Assert.That(r.Scalar($@"SELECT COUNT(*) FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" = '{r.Schema}'"), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AKeyAnotherDeployRestoresFirst_DoesNotFailThisOne()
    {
        // Two deploys in one database both find the key missing and both add it: the second ADD fails on the name. Here
        // the "other deploy" adds it in an open transaction, so this deploy's ADD waits on its lock and then fails.
        using var r = Seeded();
        r.Exec($@"CREATE TABLE ""{r.Other}"".""C"" (""Id"" INT NOT NULL, ""ParentId"" INT NULL CONSTRAINT fk_other REFERENCES ""{r.Schema}"".""Parent"" (""Id""));");
        var parse = ForgeKindler.GetParseTableJsonScript(Platform.PostgreSQL);
        r.Exec("DO $t$\nDECLARE\n  table_json TEXT = '" + r.Json("VARCHAR(60)").Replace("'", "''") + "';\n  sql_script TEXT = '';\n  p_UpdateFillFactor BOOLEAN = FALSE;\nBEGIN\n" + parse + "\nEND $t$;");
        r.Exec($@"CALL ""SchemaSmith"".""RebuildTable""('{r.Schema}', 'Parent', FALSE);
                  ALTER TABLE ""{r.Schema}"".""Parent"" ADD CONSTRAINT pk_parent PRIMARY KEY (""Id"");");
        Assert.That(r.Scalar($@"SELECT COUNT(*) FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" = '{r.Other}'"), Is.EqualTo("1"), "setup: the key is set aside");

        using var other = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        other.Open();
        other.ChangeDatabase(_mainDb);
        using (var begin = other.CreateCommand())
        {
            begin.CommandText = $@"BEGIN; ALTER TABLE ""{r.Other}"".""C"" ADD CONSTRAINT fk_other FOREIGN KEY (""ParentId"") REFERENCES ""{r.Schema}"".""Parent"" (""Id"");";
            begin.ExecuteNonQuery();
        }
        var deploy = System.Threading.Tasks.Task.Run(() => RunTableQuenchProc(r.Conn.CreateCommand(), $$"""
[ { "Schema": "{{r.Other}}", "Name": "Unrelated", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ] } ]
""", productName: $"Unrelated_{r.Schema}"));
        System.Threading.Thread.Sleep(3000);
        using (var commit = other.CreateCommand())
        {
            commit.CommandText = "COMMIT";
            commit.ExecuteNonQuery();
        }

        Assert.DoesNotThrow(() => deploy.Wait(TimeSpan.FromMinutes(2)), "the key is back, so there is nothing for this deploy to fail on");
        Assert.Multiple(() =>
        {
            Assert.That(r.Key("fk_other"), Is.EqualTo("1|1"));
            Assert.That(r.Scalar($@"SELECT COUNT(*) FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" = '{r.Other}'"), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AKeySetAsideByARunThatStoppedBeforeTheForeignKeyPass_IsRestoredByTheNextRun()
    {
        using var r = Seeded();
        r.Exec($@"CREATE TABLE ""{r.Other}"".""C"" (""Id"" INT NOT NULL, ""ParentId"" INT NULL CONSTRAINT fk_other REFERENCES ""{r.Schema}"".""Parent"" (""Id""));");
        var parse = ForgeKindler.GetParseTableJsonScript(Platform.PostgreSQL);
        r.Exec("DO $t$\nDECLARE\n  table_json TEXT = '" + r.Json("VARCHAR(60)").Replace("'", "''") + "';\n  sql_script TEXT = '';\n  p_UpdateFillFactor BOOLEAN = FALSE;\nBEGIN\n" + parse + "\nEND $t$;");
        r.Exec($@"CALL ""SchemaSmith"".""RebuildTable""('{r.Schema}', 'Parent', FALSE);");
        Assert.That(r.Scalar("SELECT COUNT(*) FROM pg_constraint WHERE conname = 'fk_other'"), Is.EqualTo("0"), "setup: the run stopped with the key set aside");

        // Another product deploys before this one runs again: the rebuilt table has no primary key yet, so the key
        // cannot come back -- and that must not fail a deploy that has nothing to do with it.
        Assert.DoesNotThrow(() => RunTableQuenchProc(r.Conn.CreateCommand(), $$"""
[ { "Schema": "{{r.Other}}", "Name": "Unrelated", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ] } ]
""", productName: $"Unrelated_{r.Schema}"));
        Assert.That(r.Scalar($@"SELECT COUNT(*) FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" = '{r.Other}'"), Is.EqualTo("1"), "and the key waits");

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Key("fk_other"), Is.EqualTo("1|1"));
            Assert.That(r.Scalar($@"SELECT COUNT(*) FROM ""SchemaSmith"".""RebuildSetAsideForeignKeys"" WHERE ""ChildSchema"" = '{r.Other}'"), Is.EqualTo("0"));
        });
    }
}
