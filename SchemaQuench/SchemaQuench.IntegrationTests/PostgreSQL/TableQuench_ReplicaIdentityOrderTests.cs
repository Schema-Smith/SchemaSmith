// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// PG-095: moving a table's replica identity off a unique index and relaxing a column of that index to NULL in the same
/// package failed, "column ... is in index used as replica identity": the nullability change ran first, and the identity
/// was set later. The identity now steps aside before the column changes.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ReplicaIdentityOrderTests : BaseTableQuenchTests
{
    [Test]
    public void AColumnOfTheReplicaIdentityIndexBecomesNullable_WhenThePackageMovesTheIdentity()
    {
        var t = $"ri_{Guid.NewGuid():N}"[..14];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(bool nullable, string identity) => $$"""
            [{ "Schema": "public", "Name": "{{t}}"{{identity}},
               "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "code", "DataType": "text", "Nullable": {{(nullable ? "true" : "false")}} } ],
               "Indexes": [ { "Name": "pk_{{t}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" },
                            { "Name": "ux_{{t}}", "Unique": true, "IndexColumns": "code" } ] }]
            """;
        string State()
        {
            cmd.CommandText = $"SELECT c.relreplident::text || '|' || (SELECT is_nullable FROM information_schema.columns WHERE table_name = '{t}' AND column_name = 'code') FROM pg_class c WHERE c.oid = 'public.{t}'::regclass";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        try
        {
            RunTableQuenchProc(cmd, Json(false, $$""", "ReplicaIdentity": "INDEX", "ReplicaIdentityIndex": "ux_{{t}}" """));
            Assert.That(State(), Is.EqualTo("i|NO"), "precondition: the identity on ux");

            RunTableQuenchProc(cmd, Json(true, """, "ReplicaIdentity": "DEFAULT" """));
            Assert.That(State(), Is.EqualTo("d|YES"), "the identity moved and the column relaxed");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{t}";
            cmd.ExecuteNonQuery();
        }
    }
}
