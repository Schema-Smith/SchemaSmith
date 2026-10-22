// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An index key carries what PostgreSQL's grammar puts there: an operator class, a per-key <c>COLLATE</c>, and
/// operator-class parameters, written in <c>IndexColumns</c> in the engine's own canonical form. A key declared with
/// any of them deploys as written and redeploys as a no-op; one declared with the default operator class is the same
/// index as one declared without it; and a changed operator class is a real change that replaces the index.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_IndexKeyCanonicalFormTests : BaseTableQuenchTests
{
    private static string Json(string table, string indexColumns, string accessMethod = "btree") => $$"""
        {
            "Schema": "public",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "name", "DataType": "text", "Nullable": true },
                { "Name": "body", "DataType": "text", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" },
                { "Name": "ix_{{table}}_k", "IndexColumns": "{{indexColumns.Replace("\"", "\\\"")}}", "AccessMethod": "{{accessMethod}}" }
            ]
        }
        """;

    private static string IndexState(IDbCommand cmd, string table) =>
        Scalar(cmd, $"SELECT i.oid::text || '|' || pg_get_indexdef(i.oid) FROM pg_class i WHERE i.relname = 'ix_{table}_k' AND i.relkind = 'i'");

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void WithTable(string table, Action<IDbCommand> body, bool needsTrgm = false)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        if (needsTrgm)
        {
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_trgm";
            cmd.ExecuteNonQuery();
        }
        try
        {
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AnOperatorClassKey_DeploysAsWritten_AndRedeploysAsANoOp()
    {
        const string table = "key_opclass";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, "name gin_trgm_ops", "gin"));
            var first = IndexState(cmd, table);
            Assert.That(first, Does.Contain("USING gin (name gin_trgm_ops)"), first);
            RunTableQuenchProc(cmd, Json(table, "name gin_trgm_ops", "gin"));
            Assert.That(IndexState(cmd, table), Is.EqualTo(first), "the redeploy neither dropped nor altered the index");
        }, needsTrgm: true);
    }

    [Test]
    public void ACollatedKey_DeploysAsWritten_AndRedeploysAsANoOp()
    {
        const string table = "key_collate";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, "name COLLATE \"C\" DESC"));
            var first = IndexState(cmd, table);
            Assert.That(first, Does.Contain("(name COLLATE \"C\" DESC)"), first);
            RunTableQuenchProc(cmd, Json(table, "name COLLATE \"C\" DESC"));
            Assert.That(IndexState(cmd, table), Is.EqualTo(first), "the redeploy neither dropped nor altered the index");
        });
    }

    [Test]
    public void AnExpressionKeyWithCollationAndOperatorClass_RedeploysAsANoOp()
    {
        const string table = "key_expr";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, "lower(name) COLLATE \"C\" text_pattern_ops"));
            var first = IndexState(cmd, table);
            Assert.That(first, Does.Contain("(lower(name) COLLATE \"C\" text_pattern_ops)"), first);
            RunTableQuenchProc(cmd, Json(table, "lower(name) COLLATE \"C\" text_pattern_ops"));
            Assert.That(IndexState(cmd, table), Is.EqualTo(first), "the redeploy neither dropped nor altered the index");
        });
    }

    [Test]
    public void OperatorClassParameters_DeployAsWritten_AndRedeployAsANoOp()
    {
        const string table = "key_params";
        WithTable(table, cmd =>
        {
            if (PgServerMajor() < 13) Assert.Ignore("Operator-class parameters are PostgreSQL 13.");
            RunTableQuenchProc(cmd, Json(table, "body gist_trgm_ops (siglen = '32')", "gist"));
            var first = IndexState(cmd, table);
            Assert.That(first, Does.Contain("gist_trgm_ops (siglen='32')"), first);
            RunTableQuenchProc(cmd, Json(table, "body gist_trgm_ops (siglen=32)", "gist"));
            Assert.That(IndexState(cmd, table), Is.EqualTo(first), "the same parameters, however spelled, are the same index");
        }, needsTrgm: true);
    }

    [Test]
    public void ADeclaredDefaultOperatorClass_IsTheSameIndex()
    {
        const string table = "key_default_opclass";
        WithTable(table, cmd =>
        {
            cmd.CommandText = $"CREATE TABLE public.{table} (id integer NOT NULL CONSTRAINT pk_{table} PRIMARY KEY, name text, body text); CREATE INDEX ix_{table}_k ON public.{table} (name)";
            cmd.ExecuteNonQuery();
            var before = IndexState(cmd, table);
            RunTableQuenchProc(cmd, Json(table, "name text_ops"));
            Assert.That(IndexState(cmd, table), Is.EqualTo(before), "text_ops is btree's default for text: nothing to change");
        });
    }

    [Test]
    public void AChangedOperatorClass_ReplacesTheIndex()
    {
        const string table = "key_change";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, "name"));
            var first = IndexState(cmd, table);
            RunTableQuenchProc(cmd, Json(table, "name text_pattern_ops"));
            var second = IndexState(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(second, Does.Contain("(name text_pattern_ops)"), second);
                Assert.That(second.Split('|')[0], Is.Not.EqualTo(first.Split('|')[0]), "a new index");
            });
            RunTableQuenchProc(cmd, Json(table, "name text_pattern_ops"));
            Assert.That(IndexState(cmd, table), Is.EqualTo(second), "and then a no-op");
        });
    }

    [Test]
    public void TheKeyFunctions_CanonicaliseAndQuoteOneSpellingOfEveryPart()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        Assert.Multiple(() =>
        {
            Assert.That(Scalar(cmd, """SELECT "SchemaSmith"."NormalizeIndexColumnList"('"name"  COLLATE  C   text_pattern_ops ( siglen = ''32'' )  DESC NULLS FIRST, "id" ASC')"""),
                Is.EqualTo("name COLLATE \"C\" text_pattern_ops (siglen=32) DESC,id"));
            Assert.That(Scalar(cmd, """SELECT "SchemaSmith"."QuoteIndexColumnList"('name COLLATE "C" text_pattern_ops (siglen=32) DESC, lower(name) COLLATE "C"')"""),
                Is.EqualTo("\"name\" COLLATE \"C\" text_pattern_ops (siglen=32) DESC,lower(name) COLLATE \"C\""));
            Assert.That(Scalar(cmd, """SELECT "SchemaSmith"."QuoteIndexColumnList"('name gin_trgm_ops,"Mixed Case" DESC')"""),
                Is.EqualTo("\"name\" gin_trgm_ops,\"Mixed Case\" DESC"));
        });
    }
}
