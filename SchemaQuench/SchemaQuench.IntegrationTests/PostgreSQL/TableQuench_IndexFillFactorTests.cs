// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An index's fill factor is emitted only when the package declares one: an index declared without it is created with
/// no <c>fillfactor</c> option, so a hash index keeps the engine's 75 instead of gaining the btree 90, and a redeploy
/// is a no-op. A declared value is applied at create and converged in place when it changes.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_IndexFillFactorTests : BaseTableQuenchTests
{
    private static string Json(string table, string indexExtra) => $$"""
        {
            "Schema": "public",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "n", "DataType": "integer", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" },
                { "Name": "ix_{{table}}_n", "IndexColumns": "n"{{indexExtra}} }
            ]
        }
        """;

    private static string IndexState(IDbCommand cmd, string index)
    {
        cmd.CommandText = $"SELECT i.oid::text || '|' || COALESCE(array_to_string(i.reloptions, ','), '-') FROM pg_class i WHERE i.relname = '{index}' AND i.relkind = 'i'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
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

    [TestCase("", TestName = "ABtreeIndexDeclaredWithoutFillFactor_GetsNoOption_AndRedeploysAsANoOp")]
    [TestCase(", \"AccessMethod\": \"hash\"", TestName = "AHashIndexDeclaredWithoutFillFactor_GetsNoOption_AndRedeploysAsANoOp")]
    public void AnIndexDeclaredWithoutFillFactor_GetsNoOption_AndRedeploysAsANoOp(string indexExtra)
    {
        var table = indexExtra == "" ? "ff_btree" : "ff_hash";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, indexExtra));
            var first = IndexState(cmd, $"ix_{table}_n");
            Assert.That(first, Does.EndWith("|-"), "no fillfactor option was written: " + first);

            RunTableQuenchProc(cmd, Json(table, indexExtra));
            Assert.That(IndexState(cmd, $"ix_{table}_n"), Is.EqualTo(first), "the redeploy neither rebuilt nor altered the index");
        });
    }

    [Test]
    public void ADeclaredFillFactor_IsApplied_AndAChangeConvergesInPlace()
    {
        const string table = "ff_declared";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, ", \"FillFactor\": 70"));
            var first = IndexState(cmd, $"ix_{table}_n");
            Assert.That(first, Does.EndWith("|fillfactor=70"), first);

            RunTableQuenchProc(cmd, Json(table, ", \"FillFactor\": 80"));
            var second = IndexState(cmd, $"ix_{table}_n");
            Assert.Multiple(() =>
            {
                Assert.That(second, Does.EndWith("|fillfactor=80"), second);
                Assert.That(second.Split('|')[0], Is.EqualTo(first.Split('|')[0]), "the change was an ALTER, not a drop and re-create");
            });
        });
    }

    [Test]
    public void AHashIndexWithTheEngineDefault_DeclaredWithoutFillFactor_IsLeftAlone()
    {
        const string table = "ff_hash_live";
        WithTable(table, cmd =>
        {
            cmd.CommandText = $"CREATE TABLE public.{table} (id integer NOT NULL CONSTRAINT pk_{table} PRIMARY KEY, n integer); CREATE INDEX ix_{table}_n ON public.{table} USING hash (n) WITH (fillfactor = 75)";
            cmd.ExecuteNonQuery();
            var before = IndexState(cmd, $"ix_{table}_n");
            RunTableQuenchProc(cmd, Json(table, ", \"AccessMethod\": \"hash\""));
            Assert.That(IndexState(cmd, $"ix_{table}_n"), Is.EqualTo(before), "an explicit 75 on a hash index is the engine default: nothing to converge");
        });
    }
}
