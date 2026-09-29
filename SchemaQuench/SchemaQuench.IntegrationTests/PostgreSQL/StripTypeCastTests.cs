// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// "SchemaSmith"."StripTypeCast" -- the helper both sides of every PostgreSQL column-default comparison go through.
/// PostgreSQL stores a default with an explicit cast, and when the column's type lives in a schema that is not on
/// the search path, the cast is schema-qualified: a tenant's enum default is stored as 'bronze'::acme.customer_tier.
/// The helper stripped a bare or quoted type name but not a qualified one, so such a column was re-altered on every
/// deploy -- every schema-template tenant with an enum or domain default, and any package typing a column by an
/// object in a non-public schema.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class StripTypeCastTests : BaseTableQuenchTests
{
    private string Strip(string text)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"SchemaSmith\".\"StripTypeCast\"(@p)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p";
        p.Value = text;
        cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() as string;
    }

    [TestCase("'bronze'::acme.customer_tier", "'bronze'")]
    [TestCase("'bronze'::\"acme\".\"customer_tier\"", "'bronze'")]
    [TestCase("'bronze'::\"Acme Tenant\".customer_tier", "'bronze'")]
    [TestCase("'{a}'::acme.tag_list[]", "'{a}'")]
    [TestCase("'x'::character varying", "'x'")]
    [TestCase("'x'::\"MyType\"", "'x'")]
    [TestCase("nextval('acme.seq'::regclass)", "nextval('acme.seq'::regclass)")]
    public void AStoredCast_IsStrippedFromTheEnd_QualifiedOrNot(string stored, string expected)
    {
        Assert.That(Strip(stored), Is.EqualTo(expected));
    }

    // The outcome the helper exists for: an enum in its own schema, a column typed by it with a default, deployed
    // twice. The second deploy must leave the column alone.
    [Test]
    public void AnEnumColumnDefault_InANonPublicSchema_IsNotReAlteredOnEveryDeploy()
    {
        var schema = $"stc_{Guid.NewGuid():N}"[..12];
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $@"CREATE SCHEMA ""{schema}""; CREATE TYPE ""{schema}"".tier AS ENUM ('bronze', 'gold');";
            cmd.ExecuteNonQuery();
            var json = $$"""
                {
                    "Schema": "{{schema}}",
                    "Name": "customer",
                    "Columns": [
                        { "Name": "id", "DataType": "integer" },
                        { "Name": "tier", "DataType": "{{schema}}.tier", "Default": "'bronze'" }
                    ]
                }
                """;
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd);

            RunTableQuenchProc(cmd, json);

            cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                                 WHERE ""SessionId"" = pg_backend_pid() AND ""ObjectType"" = 'column' AND ""ActionType"" = 'modified'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero,
                "the enum-typed column was re-altered although nothing changed");
        }
        finally
        {
            cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;";
            cmd.ExecuteNonQuery();
        }
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = @"DELETE FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""SessionId"" = pg_backend_pid()";
        cmd.ExecuteNonQuery();
    }
}
