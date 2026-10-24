// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_IdentityConvergenceTests : BaseTableQuenchTests
{
    // Regression guard for the "Alter Modified Columns" 42601 bug: a GENERATED ALWAYS
    // identity column used to be read back with its sequence's (START WITH .. INCREMENT BY ..)
    // suffix appended, so it was perpetually flagged "modified", built an empty ALTER clause,
    // and stranded a sibling clause's trailing comma -> "syntax error at end of input" (42601)
    // on any quench where a sibling column also needed altering.
    [Test]
    public void TableQuench_IdentityAlwaysColumn_WithSiblingAlter_Converges()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = $"IdentityConvergence_{uniqueId}";
        var tableName = $"price_history_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Table already exists with an ALWAYS identity PK and a sibling column whose declared
        // type differs from deployed, forcing a real ALTER clause alongside the identity column.
        cmd.CommandText = $@"
CREATE SCHEMA IF NOT EXISTS ""{schema}"";
CREATE TABLE ""{schema}"".""{tableName}"" (
  ""history_id"" INT GENERATED ALWAYS AS IDENTITY,
  ""amount"" INT NOT NULL,
  CONSTRAINT ""pk_{tableName}"" PRIMARY KEY (""history_id"")
);";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "history_id", "DataType": "INT",    "Nullable": false, "Generated": "GENERATED ALWAYS AS IDENTITY" },
        { "Name": "amount",     "DataType": "BIGINT", "Nullable": false }
    ],
    "Indexes": [
        { "Name": "pk_{{tableName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "history_id" }
    ]
}
""";

        // Pre-fix this throws 42601 on the first quench (sibling alter + phantom identity clause).
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: productName),
            "First quench must not fail on the identity-column Alter Modified Columns phase");

        // Sibling alter actually applied.
        Assert.That(GetColumnDataType(cmd, schema, tableName, "amount"), Is.EqualTo("INT8"),
            "amount should have been altered to BIGINT");

        // Identity survived the quench.
        cmd.CommandText = $@"SELECT a.attidentity FROM pg_attribute a
WHERE a.attrelid = '""{schema}"".""{tableName}""'::regclass AND a.attname = 'history_id';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("a"),
            "history_id must remain a GENERATED ALWAYS identity column");

        // Re-quench: nothing changed, so it must converge cleanly (identity no longer phantom-flagged).
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: productName),
            "Re-quench must converge without error");

        cmd.CommandText = $@"DROP TABLE ""{schema}"".""{tableName}""; DROP SCHEMA ""{schema}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Regression guard for the PostgreSQL 17 fresh-deploy bug: on PG 17 a GENERATED ALWAYS identity
    // column is still phantom-flagged "modified", but when it is the ONLY flagged column (identity +
    // plain types, no sibling column needs altering — the Chinook shape) the entire ALTER body was
    // empty, so ModifiedTableQuench emitted a bare "ALTER TABLE t\n;" -> 42601 "syntax error at end
    // of input" on the very first (fresh) deploy. Exposed when postgis:latest advanced to PG 17 and
    // the "Recreate Generated Columns (PG < 17)" step no longer runs. The ModifiedTableQuench pass
    // must emit nothing at all for such a table, not a bare header.
    [Test]
    public void TableQuench_IdentityColumn_FreshDeployNoSiblingModifications_Converges()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = $"IdentityFresh_{uniqueId}";
        var tableName = $"catalog_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Schema exists but the table does NOT — SchemaSmith creates it fresh, then ModifiedTableQuench
        // runs against it in the same quench (the exact fresh-deploy path Chinook trips).
        cmd.CommandText = $@"CREATE SCHEMA IF NOT EXISTS ""{schema}"";";
        cmd.ExecuteNonQuery();

        // Mirrors the Chinook `album` shape: the identity column's declared Generated carries the
        // sequence suffix `(START WITH .. INCREMENT BY ..)` (as SchemaTongs extracts it), which the
        // read-back strips — so the identity is perpetually flagged "modified" while nothing else
        // changes. That is the exact fresh-deploy trigger.
        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "album_id",  "DataType": "int4",         "Generated": "GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1)", "Storage": "PLAIN",    "Compression": "DEFAULT" },
        { "Name": "artist_id", "DataType": "int4",         "Generated": "NEVER", "Storage": "PLAIN",    "Compression": "DEFAULT" },
        { "Name": "title",     "DataType": "varchar(160)", "Generated": "NEVER", "Storage": "EXTENDED", "Compression": "DEFAULT" }
    ],
    "Indexes": [
        { "Name": "pk_{{tableName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "album_id", "AccessMethod": "btree", "FillFactor": 90 }
    ]
}
""";

        // Pre-fix this throws 42601 (bare ALTER TABLE header, empty body) on the fresh deploy.
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: productName),
            "Fresh deploy of an identity + plain-types table must not emit a bare ALTER TABLE header");

        cmd.CommandText = $@"SELECT a.attidentity FROM pg_attribute a
WHERE a.attrelid = '""{schema}"".""{tableName}""'::regclass AND a.attname = 'album_id';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("a"),
            "album_id must be created as a GENERATED ALWAYS identity column");

        // Re-quench: still nothing to change, must converge cleanly.
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: productName),
            "Re-quench must converge without error");

        cmd.CommandText = $@"DROP TABLE ""{schema}"".""{tableName}""; DROP SCHEMA ""{schema}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // A declared identity's sequence options were used on create and stripped by the compare, so an edited option never
    // deployed. Only the options a package writes are managed; a bare identity leaves the sequence alone.
    [Test]
    public void DeclaredIdentitySequenceOptions_AreApplied_AndABareIdentityLeavesTheSequenceAlone()
    {
        var table = $"idopt_{Guid.NewGuid():N}"[..16];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(string generated) => $$"""
            [{ "Schema": "public", "Name": "{{table}}",
               "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false, "Generated": "{{generated}}" } ] }]
            """;
        string Sequence()
        {
            cmd.CommandText = $"SELECT s.seqstart || ' ' || s.seqincrement || ' ' || s.seqcache || ' ' || s.seqcycle FROM pg_sequence s WHERE s.seqrelid = pg_get_serial_sequence('public.{table}', 'id')::regclass";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        int Modified()
        {
            cmd.CommandText = $"""SELECT COUNT(*) FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{table}.id' AND "ActionType" = 'modified'""";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        void Deploy(string generated)
        {
            cmd.CommandText = """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(generated));
        }
        try
        {
            Deploy("GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1)");
            Assert.That(Sequence(), Is.EqualTo("1 1 1 false"), "precondition");

            const string edited = "GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 5 CACHE 10 CYCLE)";
            Deploy(edited);
            Assert.Multiple(() =>
            {
                Assert.That(Sequence(), Is.EqualTo("1 5 10 true"), "the edited options");
                Assert.That(Modified(), Is.EqualTo(1));
            });
            Deploy(edited);
            Assert.That(Modified(), Is.Zero, "then a redeploy is a no-op");

            Deploy("GENERATED ALWAYS AS IDENTITY");
            Assert.Multiple(() =>
            {
                Assert.That(Sequence(), Is.EqualTo("1 5 10 true"), "a bare identity does not reset the sequence");
                Assert.That(Modified(), Is.Zero);
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    // WhatIf creates and renames nothing, so the table or column a package adds or renames is not there under its declared
    // name when the identity-options comparison runs. That comparison names only the declared column, so PostgreSQL may
    // run it before the join with the live columns that would have skipped such a column, and it looked the sequence up
    // by a name that does not exist.
    [TestCase("newTable", "GENERATED ALWAYS AS IDENTITY")]
    [TestCase("newTable", "GENERATED ALWAYS AS IDENTITY(START WITH 10 INCREMENT BY 2)")]
    [TestCase("newColumn", "GENERATED ALWAYS AS IDENTITY(START WITH 10 INCREMENT BY 2)")]
    [TestCase("renamedTable", "GENERATED ALWAYS AS IDENTITY(START WITH 10 INCREMENT BY 2)")]
    public void WhatIf_OverAnIdentityColumnNotYetUnderItsDeclaredName_PreviewsAndChangesNothing(string scenario, string generated)
    {
        var table = $"idwi_{Guid.NewGuid():N}"[..16];
        var old = $"{table}_old";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        cmd.CommandText = scenario switch
        {
            "newColumn" => $"CREATE TABLE public.{table} (n integer)",
            "renamedTable" => $"CREATE TABLE public.{old} (id integer GENERATED ALWAYS AS IDENTITY, n integer)",
            _ => "SELECT 1"
        };
        cmd.ExecuteNonQuery();
        var oldName = scenario == "renamedTable" ? $"\"OldName\": \"{old}\", " : "";
        var json = $$"""
            [{ "Schema": "public", "Name": "{{table}}", {{oldName}}
               "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false, "Generated": "{{generated}}" },
                            { "Name": "n", "DataType": "integer", "Nullable": true } ] }]
            """;
        try
        {
            RunTableQuenchProc(cmd, json, whatIf: true);

            Assert.Multiple(() =>
            {
                Assert.That(Scalar($"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = 'id'"),
                    Is.EqualTo("0"), "WhatIf added no identity column under the declared name");
                if (scenario == "renamedTable")
                    Assert.That(Scalar($"SELECT to_regclass('public.{old}') IS NOT NULL"), Is.EqualTo("True"), "and renamed nothing");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}; DROP TABLE IF EXISTS public.{old}";
            cmd.ExecuteNonQuery();
        }
    }

    // PG-042: a plain column declared identity, and an identity switched between ALWAYS and BY DEFAULT, compared as
    // changed and wrote a 'modified' audit row on every deploy, but no ALTER ever applied them.
    [Test]
    public void AnIdentityKindAndAPlainColumnBecomingIdentity_AreApplied_ThenTheRedeployIsANoOp()
    {
        var table = $"idkind_{Guid.NewGuid():N}"[..16];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(string generated) => $$"""
            [{ "Schema": "public", "Name": "{{table}}",
               "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false{{generated}} }, { "Name": "n", "DataType": "integer", "Nullable": true } ] }]
            """;
        string Kind()
        {
            cmd.CommandText = $"SELECT attidentity::text FROM pg_attribute WHERE attrelid = 'public.{table}'::regclass AND attname = 'id'";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        int Modified()
        {
            cmd.CommandText = $"""SELECT COUNT(*) FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%{table}.id' AND "ActionType" = 'modified'""";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        void Deploy(string generated)
        {
            cmd.CommandText = """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(generated));
        }
        try
        {
            Deploy("");
            cmd.CommandText = $"INSERT INTO public.{table} (id, n) VALUES (7, 1)";
            cmd.ExecuteNonQuery();

            Deploy(""", "Generated": "GENERATED BY DEFAULT AS IDENTITY (START WITH 100)" """);
            Assert.Multiple(() =>
            {
                Assert.That(Kind(), Is.EqualTo("d"), "the plain column became identity");
                Assert.That(Modified(), Is.EqualTo(1));
            });
            cmd.CommandText = $"INSERT INTO public.{table} (n) VALUES (2) RETURNING id";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(100), "with the declared sequence options, the row kept");

            Deploy(""", "Generated": "GENERATED ALWAYS AS IDENTITY" """);
            Assert.Multiple(() =>
            {
                Assert.That(Kind(), Is.EqualTo("a"), "BY DEFAULT switched to ALWAYS");
                Assert.That(Modified(), Is.EqualTo(1));
            });
            Deploy(""", "Generated": "GENERATED ALWAYS AS IDENTITY" """);
            Assert.That(Modified(), Is.Zero, "then a redeploy reports no change");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }
}
