// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A5: a table partitioned by hand, whose package declares its keys, indexes, RLS, replica identity and tablespace. The
// existence and convergence checks read relkind 'r' (and index relkind 'i'), so a partitioned parent's ('p', 'I')
// indexes and keys were "missing" on every deploy after the first (42P07), a declared PK/UNIQUE could not be added
// (USING INDEX is refused on a partitioned table), REPLICA IDENTITY USING INDEX found no index, and RLS, tablespace and
// replica identity never converged.
[Category("PostgreSQL")]
[NonParallelizable]
public class TableQuench_PartitionedParentTests : BaseTableQuenchTests
{
    private const string Tablespace = "ss_part_ts";
    private const string TablespacePath = "/tmp/ss_part_ts";
    private bool _tablespaceReady;

    private NpgsqlConnection Open(string db)
    {
        var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    [OneTimeSetUp]
    public void CreateTablespace()
    {
        using var maint = Open("postgres");
        try { Exec(maint, $"COPY (SELECT 1) TO PROGRAM 'mkdir -p {TablespacePath}'"); } catch (DbException) { }
        try { Exec(maint, $"CREATE TABLESPACE {Tablespace} LOCATION '{TablespacePath}'"); _tablespaceReady = true; } catch (DbException) { }
    }

    [OneTimeTearDown]
    public void DropTablespace()
    {
        if (!_tablespaceReady) return;
        using var maint = Open("postgres");
        Exec(maint, $"DROP TABLESPACE IF EXISTS {Tablespace}");
        try { Exec(maint, $"COPY (SELECT 1) TO PROGRAM 'rmdir {TablespacePath}'"); } catch (DbException) { }
    }

    private string Json(string table, string tablespace) => $$"""
        [{ "Schema": "public", "Name": "{{table}}", "RowLevelSecurity": true, "FillFactor": 80, "UpdateFillFactor": true,
           "ReplicaIdentity": "INDEX", "ReplicaIdentityIndex": "pk_{{table}}"{{(tablespace == null ? "" : $", \"Tablespace\": \"{tablespace}\"")}},
           "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false },
                        { "Name": "d", "DataType": "date", "Nullable": false },
                        { "Name": "code", "DataType": "text", "Nullable": true },
                        { "Name": "doc", "DataType": "jsonb", "Nullable": true } ],
           "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id,d" },
                        { "Name": "uq_{{table}}_code", "UniqueConstraint": true, "Unique": true, "IndexColumns": "code,d" },
                        { "Name": "ix_{{table}}_code", "IndexColumns": "code" },
                        { "Name": "ix_{{table}}_doc", "AccessMethod": "gin", "IndexColumns": "doc" } ] }]
        """;

    [Test]
    public void APartitionedParent_DeploysItsDeclaredKeysAndSettings_AndTheSecondDeployChangesNothing()
    {
        var table = "part_parent_" + Guid.NewGuid().ToString("N")[..8];
        var product = "PartParent_" + table;
        var tablespace = _tablespaceReady ? Tablespace : null;
        using var conn = Open(_mainDb);
        try
        {
            Exec(conn, $"CREATE TABLE public.{table} (id integer NOT NULL, d date NOT NULL, code text, doc jsonb) PARTITION BY RANGE (d); " +
                       $"CREATE TABLE public.{table}_2025 PARTITION OF public.{table} FOR VALUES FROM ('2025-01-01') TO ('2026-01-01'); " +
                       $"CREATE TABLE public.{table}_2026 PARTITION OF public.{table} FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');");

            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, tablespace), productName: product), "the first deploy");

            Assert.Multiple(() =>
            {
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM pg_constraint WHERE conrelid = 'public.{table}'::regclass AND contype IN ('p', 'u')"), Is.EqualTo("2"), "PK and UNIQUE on the parent");
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM pg_index WHERE indrelid = 'public.{table}'::regclass"), Is.EqualTo("4"), "every declared index");
                Assert.That(Scalar(conn, $"SELECT am.amname FROM pg_class i JOIN pg_am am ON am.oid = i.relam WHERE i.relname = 'ix_{table}_doc'"), Is.EqualTo("gin"));
                Assert.That(Scalar(conn, $"SELECT relrowsecurity::text FROM pg_class WHERE oid = 'public.{table}'::regclass"), Is.EqualTo("true"), "RLS");
                Assert.That(Scalar(conn, $"SELECT relreplident::text FROM pg_class WHERE oid = 'public.{table}'::regclass"), Is.EqualTo("i"), "replica identity");
                if (tablespace != null)
                    Assert.That(Scalar(conn, $"SELECT t.spcname FROM pg_class c JOIN pg_tablespace t ON t.oid = c.reltablespace WHERE c.oid = 'public.{table}'::regclass"), Is.EqualTo(tablespace));
            });

            Exec(conn, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, tablespace), productName: product), "the second deploy");
            var changes = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT \"ObjectType\" || ' ' || \"ObjectName\" || ' ' || \"ActionType\" FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()";
                using var r = cmd.ExecuteReader();
                while (r.Read()) changes.Add(r.GetString(0));
            }
            Assert.That(changes, Is.Empty, "an unchanged package changes nothing on a partitioned parent");
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{table} CASCADE");
            Exec(conn, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }

    // The parent holds no rows, so its tablespace is only where new partitions go: a declared one is set, not refused as
    // a move, and the partitions already there stay where they are.
    [Test]
    public void APartitionedParentsDeclaredTablespace_IsSet_AndItsPartitionsStay()
    {
        if (!_tablespaceReady) Assert.Ignore("CREATE TABLESPACE is not permitted on this server.");
        var table = "part_ts_" + Guid.NewGuid().ToString("N")[..8];
        var product = "PartTs_" + table;
        using var conn = Open(_mainDb);
        try
        {
            Exec(conn, $"CREATE TABLE public.{table} (id integer NOT NULL, d date NOT NULL, code text, doc jsonb) PARTITION BY RANGE (d); " +
                       $"CREATE TABLE public.{table}_2025 PARTITION OF public.{table} FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, Tablespace), productName: product));
            Exec(conn, $"CREATE TABLE public.{table}_2026 PARTITION OF public.{table} FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');");
            string SpaceOf(string rel) => Scalar(conn, $"SELECT COALESCE(t.spcname, '(default)') FROM pg_class c LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace WHERE c.oid = 'public.{rel}'::regclass");
            Assert.Multiple(() =>
            {
                Assert.That(SpaceOf(table), Is.EqualTo(Tablespace), "the parent's default is set");
                Assert.That(SpaceOf(table + "_2025"), Is.EqualTo("(default)"), "a partition that was there is not moved");
                Assert.That(SpaceOf(table + "_2026"), Is.EqualTo(Tablespace), "a new partition goes where the parent says");
            });
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{table} CASCADE");
            Exec(conn, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }

    // A partition declared as a table of its own, without the index it inherits from its parent's: that index cannot be
    // dropped (the parent's index requires it), so DropUnknownIndexes leaves it instead of failing the deploy.
    [Test]
    public void DropUnknownIndexes_LeavesAnIndexAPartitionInheritsFromItsParent()
    {
        var table = "part_inh_" + Guid.NewGuid().ToString("N")[..8];
        var product = "PartInh_" + table;
        using var conn = Open(_mainDb);
        try
        {
            Exec(conn, $"CREATE TABLE public.{table} (id integer NOT NULL, d date NOT NULL) PARTITION BY RANGE (d); " +
                       $"CREATE TABLE public.{table}_2025 PARTITION OF public.{table} FOR VALUES FROM ('2025-01-01') TO ('2026-01-01'); " +
                       $"CREATE INDEX ix_{table}_id ON public.{table} (id);");
            var json = $$"""
                [{ "Schema": "public", "Name": "{{table}}_2025",
                   "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "d", "DataType": "date", "Nullable": false } ] }]
                """;
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, indexOnly: true, productName: product));
            Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM pg_index WHERE indrelid = 'public.{table}_2025'::regclass"), Is.EqualTo("1"), "the inherited index is still there");
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{table} CASCADE");
            Exec(conn, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }

    [Test]
    public void IndexOnly_OnAPartitionedParent_RunsTwiceWithoutRecreatingAnything()
    {
        var table = "part_io_" + Guid.NewGuid().ToString("N")[..8];
        var product = "PartIo_" + table;
        using var conn = Open(_mainDb);
        try
        {
            Exec(conn, $"CREATE TABLE public.{table} (id integer NOT NULL, d date NOT NULL, code text, doc jsonb) PARTITION BY RANGE (d); " +
                       $"CREATE TABLE public.{table}_2025 PARTITION OF public.{table} FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, null), indexOnly: true, productName: product), "first");
            Exec(conn, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, null), indexOnly: true, productName: product), "second");
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM pg_index WHERE indrelid = 'public.{table}'::regclass"), Is.EqualTo("4"));
                Assert.That(Scalar(conn, "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()"), Is.EqualTo("0"), "the second run changes nothing");
            });
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{table} CASCADE");
            Exec(conn, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }

    // A foreign key into a partitioned table is cloned onto the referencing table once per partition (conparentid <> 0,
    // named fk_1, fk_2 on 18 and fk_col_fkey, fk_col_fkey1 on 12). Read as keys of their own they are undeclared, so a
    // deploy would drop them, or compare them, every time.
    [Test]
    public void AForeignKeyIntoAPartitionedTable_IsOneKey_AndTheSecondDeployChangesNothing()
    {
        var table = "part_fk_" + Guid.NewGuid().ToString("N")[..8];
        var product = "PartFk_" + table;
        using var conn = Open(_mainDb);
        try
        {
            Exec(conn, $"CREATE TABLE public.{table} (id integer NOT NULL, d date NOT NULL) PARTITION BY RANGE (d); " +
                       $"CREATE TABLE public.{table}_2025 PARTITION OF public.{table} FOR VALUES FROM ('2025-01-01') TO ('2026-01-01'); " +
                       $"CREATE TABLE public.{table}_2026 PARTITION OF public.{table} FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');");
            var json = $$"""
                [{ "Schema": "public", "Name": "{{table}}",
                   "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "d", "DataType": "date", "Nullable": false } ],
                   "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id,d" } ] },
                 { "Schema": "public", "Name": "{{table}}_ref",
                   "Columns": [ { "Name": "rid", "DataType": "integer", "Nullable": false }, { "Name": "id", "DataType": "integer", "Nullable": true },
                                { "Name": "d", "DataType": "date", "Nullable": true } ],
                   "Indexes": [ { "Name": "pk_{{table}}_ref", "PrimaryKey": true, "Unique": true, "IndexColumns": "rid" } ],
                   "ForeignKeys": [ { "Name": "fk_{{table}}_ref", "Columns": "id,d", "RelatedTableSchema": "public", "RelatedTable": "{{table}}", "RelatedColumns": "id,d" } ] }]
                """;
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product), "first");
            Exec(conn, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product), "second");
            var changes = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT \"ObjectType\" || ' ' || \"ObjectName\" || ' ' || \"ActionType\" FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()";
                using var r = cmd.ExecuteReader();
                while (r.Read()) changes.Add(r.GetString(0));
            }
            // A rebuild of the referencing table carries its keys over: the one key, which the engine clones again, not
            // the clones as keys of their own.
            var rebuilt = json.Replace($"\"Name\": \"{table}_ref\",", $"\"Name\": \"{table}_ref\", \"RebuildPolicy\": {{ \"Mode\": \"ALWAYS\" }},")
                              .Replace("\"Name\": \"rid\", \"DataType\": \"integer\"", "\"Name\": \"rid\", \"DataType\": \"bigint\"");
            using (var cmd = conn.CreateCommand())
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, rebuilt, productName: product), "a rebuild of the referencing table");
            Assert.Multiple(() =>
            {
                Assert.That(changes, Is.Empty, "the second deploy changes nothing");
                Assert.That(Scalar(conn, $"SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.{table}_ref'::regclass AND attname = 'rid'"), Is.EqualTo("bigint"), "the rebuild ran");
                Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM pg_constraint WHERE conrelid = 'public.{table}_ref'::regclass AND contype = 'f' AND conparentid = 0"), Is.EqualTo("1"));
                Assert.That(Scalar(conn, $"SELECT json_array_length((\"SchemaSmith\".\"GenerateTableJSON\"('public', '{table}_ref')::json) -> 'ForeignKeys')"), Is.EqualTo("1"),
                    "extraction writes the one key, not its clones");
            });
        }
        finally
        {
            Exec(conn, $"DROP TABLE IF EXISTS public.{table}_ref; DROP TABLE IF EXISTS public.{table} CASCADE");
            Exec(conn, $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{product}'");
        }
    }
}
