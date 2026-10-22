// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// An exclusion constraint carries the same extras as an index: covering columns (<c>INCLUDE</c>), storage
/// parameters (<c>WITH</c>) and an operator class on a key. Declared, they are written into the constraint; a redeploy
/// is a no-op; a change to the storage parameters replaces the constraint.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ExcludeConstraintExtrasTests : BaseTableQuenchTests
{
    private static string Json(string table, string extras) => $$"""
        {
            "Schema": "public",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "integer", "Nullable": false },
                { "Name": "room", "DataType": "integer", "Nullable": false },
                { "Name": "during", "DataType": "tsrange", "Nullable": false },
                { "Name": "note", "DataType": "text", "Nullable": true }
            ],
            "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" } ],
            "ExcludeConstraints": [
                { "Name": "ex_{{table}}", "AccessMethod": "gist",
                  "ExcludeColumns": [ { "Column": "room", "Operator": "=" }, { "Column": "during", "Operator": "&&" } ]{{extras}} }
            ]
        }
        """;

    private const string Extras = ", \"IncludeColumns\": \"note\", \"StorageParameters\": { \"fillfactor\": \"70\" }";

    private static string ConstraintState(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT con.oid::text || '|' || pg_get_constraintdef(con.oid) || '|' || COALESCE(array_to_string(i.reloptions, ','), '-') FROM pg_constraint con JOIN pg_class i ON i.oid = con.conindid WHERE con.conname = 'ex_{table}' AND con.contype = 'x'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS btree_gist";
        cmd.ExecuteNonQuery();
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
    public void IncludeAndStorageParameters_AreWritten_AndRedeployAsANoOp()
    {
        const string table = "excl_extras";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, Extras));
            var first = ConstraintState(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(first, Does.Contain("INCLUDE (note)"), first);
                Assert.That(first, Does.EndWith("|fillfactor=70"), first);
            });
            RunTableQuenchProc(cmd, Json(table, Extras));
            Assert.That(ConstraintState(cmd, table), Is.EqualTo(first), "the redeploy neither dropped nor altered the constraint");

            RunTableQuenchProc(cmd, Json(table, ", \"IncludeColumns\": \"note\", \"StorageParameters\": { \"fillfactor\": \"80\" }"));
            var second = ConstraintState(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(second, Does.EndWith("|fillfactor=80"), second);
                Assert.That(second.Split('|')[0], Is.Not.EqualTo(first.Split('|')[0]), "a changed storage parameter replaces the constraint");
            });
        });
    }

    [Test]
    public void AnExclusionWithoutExtras_IsLeftAlone_WhenNoneAreDeclared()
    {
        const string table = "excl_plain";
        WithTable(table, cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, ""));
            var first = ConstraintState(cmd, table);
            Assert.That(first, Does.EndWith("|-"), first);
            RunTableQuenchProc(cmd, Json(table, ""));
            Assert.That(ConstraintState(cmd, table), Is.EqualTo(first));
        });
    }
}
