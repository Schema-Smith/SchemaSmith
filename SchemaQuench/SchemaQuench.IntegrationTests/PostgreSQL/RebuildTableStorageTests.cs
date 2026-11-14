// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A table rebuild created its replacement bare: an unlogged table came back logged, a table on another tablespace
// came back on the default, a table using another access method came back on heap, and its storage parameters were
// gone -- and a table that DECLARED its tablespace failed the placement check that follows the rebuild. The
// replacement is now created with the table's storage, declared where the package declares it, as it was otherwise.
[Category("PostgreSQL")]
[NonParallelizable]
public class RebuildTableStorageTests : BaseTableQuenchTests
{
    private readonly string _fixtureUid = Guid.NewGuid().ToString("N")[..8];
    private string Tablespace => $"ss_rb_{_fixtureUid}";
    private string TablespacePath => $"/var/lib/postgresql/ss_rb_{_fixtureUid}";
    private string AccessMethod => $"ss_heap_{_fixtureUid}";
    private bool _tablespaceReady;

    private IDbConnection Open(string db)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        if (db != "postgres") conn.ChangeDatabase(db);
        return conn;
    }

    private static void Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    [OneTimeSetUp]
    public void CreateTablespaceAndAccessMethod()
    {
        using var maint = Open("postgres");
        // CREATE TABLESPACE needs a directory that already exists; COPY ... TO PROGRAM makes it, as TablespaceTests does.
        try { Exec(maint, $"COPY (SELECT 1) TO PROGRAM 'mkdir -p {TablespacePath}'"); } catch (DbException) { }
        try { Exec(maint, $"CREATE TABLESPACE {Tablespace} LOCATION '{TablespacePath}'"); _tablespaceReady = true; } catch (DbException) { }
        using var main = Open(_mainDb);
        Exec(main, $"CREATE ACCESS METHOD {AccessMethod} TYPE TABLE HANDLER heap_tableam_handler");
    }

    [OneTimeTearDown]
    public void DropTablespaceAndAccessMethod()
    {
        using (var main = Open(_mainDb))
            Exec(main, $"DROP ACCESS METHOD IF EXISTS {AccessMethod}");
        using var maint = Open("postgres");
        if (!_tablespaceReady) return;
        Exec(maint, $"DROP TABLESPACE IF EXISTS {Tablespace}");
        try { Exec(maint, $"COPY (SELECT 1) TO PROGRAM 'rmdir {TablespacePath}'"); } catch (DbException) { }
    }

    private sealed class Run : IDisposable
    {
        public readonly IDbConnection Conn;
        public readonly string Schema = $"rbs_{Guid.NewGuid().ToString("N")[..8]}";

        public Run(IDbConnection conn)
        {
            Conn = conn;
            Exec(Conn, $@"CREATE SCHEMA ""{Schema}"";");
        }

        public string Json(string valType, string extra = "") => $$"""
[ { "Schema": "{{Schema}}", "Name": "Parent", "RebuildPolicy": { "Mode": "ALWAYS" }{{extra}},
    "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Val", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ { "Name": "pk_parent", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] } ]
""";

        public string Parent => $"'\"{Schema}\".\"Parent\"'::regclass";
        public string Oid() => Scalar(Conn, $"SELECT {Parent}::oid");
        public string Get(string expression) => Scalar(Conn, $"SELECT {expression} FROM pg_class c WHERE c.oid = {Parent}");

        public void Dispose()
        {
            Exec(Conn, $@"DROP SCHEMA IF EXISTS ""{Schema}"" CASCADE;");
            Conn.Dispose();
        }
    }

    // The table is created by hand with the storage under test, then a deploy changes a column and the policy rebuilds.
    private Run Seeded(string storage = "")
    {
        var r = new Run(Open(_mainDb));
        Exec(r.Conn, $@"CREATE TABLE ""{r.Schema}"".""Parent"" (""Id"" INT NOT NULL CONSTRAINT pk_parent PRIMARY KEY, ""Val"" VARCHAR(50)) {storage};
                        INSERT INTO ""{r.Schema}"".""Parent"" VALUES (1, 'a'), (2, 'b');");
        return r;
    }

    private void Deploy(Run r, string json) => RunTableQuenchProc(r.Conn.CreateCommand(), json);

    [Test]
    public void AnUnloggedTable_StaysUnlogged()
    {
        using var r = Seeded();
        Exec(r.Conn, $@"ALTER TABLE ""{r.Schema}"".""Parent"" SET UNLOGGED;");
        var before = r.Oid();

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Oid(), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Get("c.relpersistence"), Is.EqualTo("u"));
            Assert.That(Scalar(r.Conn, $@"SELECT COUNT(*) FROM ""{r.Schema}"".""Parent"""), Is.EqualTo("2"));
        });
    }

    [Test]
    public void TheTablesStorageParameters_AreKept_ItsToastTablesToo()
    {
        using var r = Seeded();
        // A column that can be toasted, so the table has a toast table for its toast.* parameters to live on.
        Exec(r.Conn, $@"ALTER TABLE ""{r.Schema}"".""Parent"" ALTER ""Val"" TYPE TEXT;
                        ALTER TABLE ""{r.Schema}"".""Parent"" SET (autovacuum_enabled = false, toast.autovacuum_enabled = false);");
        var before = r.Oid();

        Deploy(r, r.Json("VARCHAR(5000)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Oid(), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Get("array_to_string(c.reloptions, ',')"), Is.EqualTo("autovacuum_enabled=false"));
            Assert.That(Scalar(r.Conn, $"SELECT array_to_string(t.reloptions, ',') FROM pg_class c JOIN pg_class t ON t.oid = c.reltoastrelid WHERE c.oid = {r.Parent}"),
                Is.EqualTo("autovacuum_enabled=false"));
        });
    }

    [Test]
    public void ADeclaredFillFactor_IsWrittenIntoTheReplacement()
    {
        using var r = Seeded();
        var json = r.Json("VARCHAR(60)", @", ""UpdateFillFactor"": true, ""FillFactor"": 70");
        var parse = ForgeKindler.GetParseTableJsonScript(Platform.PostgreSQL);
        Exec(r.Conn, "DO $t$\nDECLARE\n  table_json TEXT = '" + json.Replace("'", "''") + "';\n  sql_script TEXT = '';\n  p_UpdateFillFactor BOOLEAN = FALSE;\nBEGIN\n" + parse + "\nEND $t$;");

        Exec(r.Conn, $@"CALL ""SchemaSmith"".""RebuildTable""('{r.Schema}', 'Parent', FALSE);");

        Assert.That(r.Get("array_to_string(c.reloptions, ',')"), Is.EqualTo("fillfactor=70"),
            "the copy writes its pages at the declared fillfactor, not at 100 with the setting applied afterwards");
    }

    [Test]
    public void ATableUsingAnotherAccessMethod_KeepsIt()
    {
        using var r = Seeded($"USING {AccessMethod}");
        var before = r.Oid();

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Oid(), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Get("(SELECT amname FROM pg_am WHERE oid = c.relam)"), Is.EqualTo(AccessMethod));
        });
    }

    [Test]
    public void ATableOnAnotherTablespace_StaysThere()
    {
        if (!_tablespaceReady) Assert.Ignore("CREATE TABLESPACE is not permitted on this server.");
        using var r = Seeded($"TABLESPACE {Tablespace}");
        var before = r.Oid();

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Oid(), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Get("(SELECT spcname FROM pg_tablespace WHERE oid = c.reltablespace)"), Is.EqualTo(Tablespace));
        });
    }

    [Test]
    public void ATableThatDeclaresItsTablespace_Rebuilds_AndStaysThere()
    {
        if (!_tablespaceReady) Assert.Ignore("CREATE TABLESPACE is not permitted on this server.");
        using var r = Seeded($"TABLESPACE {Tablespace}");
        var before = r.Oid();

        Assert.DoesNotThrow(() => Deploy(r, r.Json("VARCHAR(60)", $@", ""Tablespace"": ""{Tablespace}""")),
            "created on the default, the replacement failed the placement check that refuses to move a table");

        Assert.Multiple(() =>
        {
            Assert.That(r.Oid(), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(r.Get("(SELECT spcname FROM pg_tablespace WHERE oid = c.reltablespace)"), Is.EqualTo(Tablespace));
        });
    }

    [Test]
    public void ATableThatDeclaresAnotherTablespace_IsBuiltThere_SinceARebuildRewritesItAnyway()
    {
        if (!_tablespaceReady) Assert.Ignore("CREATE TABLESPACE is not permitted on this server.");
        using var r = Seeded();

        Assert.DoesNotThrow(() => Deploy(r, r.Json("VARCHAR(60)", $@", ""Tablespace"": ""{Tablespace}""")),
            "in place this is a move, refused because it is a rewrite; a rebuild is a rewrite already");

        Assert.That(r.Get("(SELECT spcname FROM pg_tablespace WHERE oid = c.reltablespace)"), Is.EqualTo(Tablespace));
    }
}
