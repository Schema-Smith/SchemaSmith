// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A unique constraint dropped CASCADE takes every foreign key that references it. A full deploy put back only the keys
// the package declares and an index-only deploy put back none, so a key created outside the package was lost; and a
// plain unique index a key references could not be dropped at all, failing the deploy. A deploy now never drops a key
// it will not re-create: an index such a key references is kept as it is.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ForeignKeySafetyTests : BaseTableQuenchTests
{
    private sealed record Tables(string Parent, string Suffix);

    private static Tables Create(IDbCommand cmd)
    {
        var u = Guid.NewGuid().ToString("N")[..8];
        var t = new Tables($"fksafe_parent_{u}", u);
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"
CREATE TABLE public.{t.Parent} (id integer NOT NULL CONSTRAINT pk_{u} PRIMARY KEY, code integer NOT NULL, alt integer NOT NULL, other integer NOT NULL,
  CONSTRAINT uqc_alt_{u} UNIQUE (alt));
CREATE UNIQUE INDEX uqi_code_{u} ON public.{t.Parent} (code);
CREATE UNIQUE INDEX uqi_other_{u} ON public.{t.Parent} (other);
CREATE TABLE public.fksafe_bycode_{u} (id integer PRIMARY KEY, parent_code integer NOT NULL CONSTRAINT fk_bycode_{u} REFERENCES public.{t.Parent} (code));
CREATE TABLE public.fksafe_byalt_{u} (id integer PRIMARY KEY, parent_alt integer NOT NULL CONSTRAINT fk_byalt_{u} REFERENCES public.{t.Parent} (alt));
CREATE TABLE public.fksafe_byid_{u} (id integer PRIMARY KEY, parent_id integer NOT NULL CONSTRAINT fk_byid_{u} REFERENCES public.{t.Parent} (id));";
        cmd.ExecuteNonQuery();
        return t;
    }

    private static void Drop(IDbCommand cmd, Tables t)
    {
        var u = t.Suffix;
        cmd.CommandText = $"DROP TABLE IF EXISTS public.fksafe_bycode_{u}, public.fksafe_byalt_{u}, public.fksafe_byid_{u}, public.{t.Parent};";
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    private static string ForeignKeys(IDbCommand cmd, Tables t) => Scalar(cmd,
        $"SELECT STRING_AGG(conname, ',' ORDER BY conname) FROM pg_constraint WHERE contype = 'f' AND confrelid = 'public.{t.Parent}'::regclass");

    private static string ParentIndexes(IDbCommand cmd, Tables t) => Scalar(cmd,
        $"SELECT STRING_AGG(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{t.Parent}'");

    private static string ParentJson(Tables t, string extraIndexes = "") => $$"""
        [{ "Schema": "public", "Name": "{{t.Parent}}",
           "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "code", "DataType": "integer", "Nullable": false },
                        { "Name": "alt", "DataType": "integer", "Nullable": false }, { "Name": "other", "DataType": "integer", "Nullable": false } ],
           "Indexes": [ { "Name": "pk_{{t.Suffix}}", "PrimaryKey": true, "IndexColumns": "id" }{{extraIndexes}} ] }]
        """;

    private void Run(IDbCommand cmd, bool indexOnly, string json)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = indexOnly
            ? $"CALL \"SchemaSmith\".\"IndexOnlyQuench\"(p_ProductName := 'FkSafety', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropUnknownIndexes := true);"
            : $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := 'FkSafety', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := false, p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := true);";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private IDbCommand Open(out IDbConnection conn)
    {
        conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn.CreateCommand();
    }

    [TestCase(false, TestName = "FullDeploy_DroppingUnknownUniqueIndexes_KeepsForeignKeysOutsideThePackage")]
    [TestCase(true, TestName = "IndexOnly_DroppingUnknownUniqueIndexes_NeverDropsAForeignKey")]
    public void DroppingUnknownUniqueIndexes_KeepsForeignKeysOutsideThePackage(bool indexOnly)
    {
        using var cmd = Open(out var conn);
        using var _ = conn;
        var t = Create(cmd);
        var u = t.Suffix;
        try
        {
            Assert.DoesNotThrow(() => Run(cmd, indexOnly, ParentJson(t)));

            Assert.Multiple(() =>
            {
                Assert.That(ForeignKeys(cmd, t), Is.EqualTo($"fk_byalt_{u},fk_bycode_{u},fk_byid_{u}"), "keys outside the package are not the deploy's to drop");
                Assert.That(ParentIndexes(cmd, t), Is.EqualTo($"pk_{u},uqc_alt_{u},uqi_code_{u}"),
                    "the constraint and the index keys reference are kept; the unreferenced unknown index is dropped");
            });
        }
        finally
        {
            Drop(cmd, t);
        }
    }

    [TestCase(false, TestName = "FullDeploy_ChangingAUniqueConstraintAKeyOutsideThePackageReferences_KeepsBoth")]
    [TestCase(true, TestName = "IndexOnly_ChangingAUniqueConstraintAForeignKeyReferences_KeepsBoth")]
    public void ChangingAReferencedUniqueConstraint_KeepsItAndTheKey(bool indexOnly)
    {
        using var cmd = Open(out var conn);
        using var _ = conn;
        var messages = new List<string>();
        ((NpgsqlConnection)conn).Notice += (_, e) => messages.Add(e.Notice.MessageText);
        var t = Create(cmd);
        var u = t.Suffix;
        try
        {
            var declared = $$""", { "Name": "uqc_alt_{{u}}", "UniqueConstraint": true, "IndexColumns": "alt, other" }, { "Name": "uqi_code_{{u}}", "Unique": true, "IndexColumns": "code" }, { "Name": "uqi_other_{{u}}", "Unique": true, "IndexColumns": "other" }""";
            Assert.DoesNotThrow(() => Run(cmd, indexOnly, ParentJson(t, declared)));

            Assert.Multiple(() =>
            {
                Assert.That(ForeignKeys(cmd, t), Is.EqualTo($"fk_byalt_{u},fk_bycode_{u},fk_byid_{u}"));
                Assert.That(Scalar(cmd, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'uqc_alt_{u}'"),
                    Is.EqualTo("UNIQUE (alt)"), "a key the deploy will not re-create references it, so it is kept as it is");
                Assert.That(messages, Has.Some.Matches<string>(m => m.TrimStart().StartsWith("WARNING: Kept ") && m.Contains($"uqc_alt_{u}") && m.Contains($"fk_byalt_{u}")),
                    "a declared change the deploy did not apply is a warning, naming the constraint and the key that holds it: " + string.Join(" | ", messages));
            });
        }
        finally
        {
            Drop(cmd, t);
        }
    }
}
