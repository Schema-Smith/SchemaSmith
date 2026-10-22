// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// PG-041: a foreign key's <c>Deferrable</c>, <c>InitiallyDeferred</c> and <c>MatchType</c> were created but never compared,
/// so an edited package left the old key in place at exit 0. Deferral now changes in place (<c>ALTER CONSTRAINT</c>, no
/// re-validation: the key keeps its oid); a match type, which PostgreSQL cannot alter, re-creates the key.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ForeignKeyConvergenceTests : BaseTableQuenchTests
{
    private static string Json(string t, string fkOptions) => $$"""
        [{ "Schema": "public", "Name": "{{t}}_p",
           "Columns": [ { "Name": "a", "DataType": "integer", "Nullable": false }, { "Name": "b", "DataType": "integer", "Nullable": false } ],
           "Indexes": [ { "Name": "pk_{{t}}_p", "PrimaryKey": true, "Unique": true, "IndexColumns": "a,b" } ] },
         { "Schema": "public", "Name": "{{t}}_c",
           "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "integer", "Nullable": true }, { "Name": "b", "DataType": "integer", "Nullable": true } ],
           "Indexes": [ { "Name": "pk_{{t}}_c", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ],
           "ForeignKeys": [ { "Name": "fk_{{t}}", "Columns": "a,b", "RelatedTableSchema": "public", "RelatedTable": "{{t}}_p", "RelatedColumns": "a,b"{{fkOptions}} } ] }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    // oid|deferrable|deferred|match: a re-created key has a new oid.
    private static string Key(IDbCommand cmd, string t) => Scalar(cmd,
        $"SELECT oid::text || '|' || condeferrable || '|' || condeferred || '|' || confmatchtype::text FROM pg_constraint WHERE conname = 'fk_{t}'");

    private static int Audits(IDbCommand cmd, string t) => Convert.ToInt32(Scalar(cmd,
        $"""SELECT COUNT(*) FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid() AND "ObjectName" LIKE '%fk_{t}%'"""));

    [Test]
    public void AnEditedForeignKey_TakesTheDeclaredDeferralInPlace_AndItsMatchTypeByReCreation()
    {
        var t = $"fkconv_{Guid.NewGuid():N}"[..16];
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        void Deploy(string options)
        {
            cmd.CommandText = """DELETE FROM "SchemaSmith"."ChangeAudit" WHERE "SessionId" = pg_backend_pid()""";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(t, options));
        }
        try
        {
            Deploy("");
            var created = Key(cmd, t).Split('|');
            Assert.That(created[1..], Is.EqualTo(new[] { "false", "false", "s" }), "precondition");

            const string deferred = """, "Deferrable": true, "InitiallyDeferred": true""";
            Deploy(deferred);
            var altered = Key(cmd, t).Split('|');
            Assert.Multiple(() =>
            {
                Assert.That(altered[1..], Is.EqualTo(new[] { "true", "true", "s" }), "the declared deferral");
                Assert.That(altered[0], Is.EqualTo(created[0]), "changed in place, not re-created");
                Assert.That(Audits(cmd, t), Is.EqualTo(1));
            });
            Deploy(deferred);
            Assert.That(Audits(cmd, t), Is.Zero, "then a redeploy is a no-op");

            const string full = """, "Deferrable": true, "InitiallyDeferred": true, "MatchType": "FULL" """;
            Deploy(full);
            var matched = Key(cmd, t).Split('|');
            Assert.Multiple(() =>
            {
                Assert.That(matched[1..], Is.EqualTo(new[] { "true", "true", "f" }), "the declared match type, with the deferral kept");
                Assert.That(matched[0], Is.Not.EqualTo(created[0]), "a match type is re-created");
            });
            Deploy(full);
            Assert.That(Audits(cmd, t), Is.Zero, "then a redeploy is a no-op");

            Deploy(""", "MatchType": "FULL" """);
            Assert.That(Key(cmd, t).Split('|')[1..], Is.EqualTo(new[] { "false", "false", "f" }), "and back to NOT DEFERRABLE");
        }
        finally
        {
            cmd.CommandText = $"""DROP TABLE IF EXISTS public."{t}_c"; DROP TABLE IF EXISTS public."{t}_p";""";
            cmd.ExecuteNonQuery();
        }
    }
}
