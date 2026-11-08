// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// A policy role is an identifier. The declared role kept its quotes while both sides were lower-cased, so a quoted
// mixed-case role never matched and the policy was dropped and re-created on every deploy, leaving the table without it
// for a moment each run. Extraction wrote the role unquoted, so it deployed folded to lower case.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PolicyRoleCaseTests : BaseTableQuenchTests
{
    // With PUBLIC in the list PostgreSQL ignores the other roles ("ignoring specified roles other than PUBLIC") and stores
    // PUBLIC alone, so that declaration churned as well.
    [TestCase(false)]
    [TestCase(true)]
    public void AQuotedMixedCaseRole_RedeploysWithoutReplacingThePolicy_AndExtractsQuoted(bool withPublic)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"pol_role_{suffix}";
        var role = $"AppUser_{suffix}";
        var json = $$"""
            {
                "Schema": "public",
                "Name": "{{table}}",
                "RowLevelSecurity": true,
                "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
                "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" } ],
                "Policies": [ { "Name": "pol_{{suffix}}", "Command": "SELECT", "Roles": "\"{{role}}\"{{(withPublic ? ", PUBLIC" : "")}}", "UsingExpression": "id > 0" } ]
            }
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE ROLE \"{role}\"";
        cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT oid FROM pg_policy WHERE polname = 'pol_{suffix}'";
            var before = cmd.ExecuteScalar();
            Assert.That(before, Is.Not.Null, "setup: the first deploy created the policy");

            cmd.CommandText = "DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid()";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT COALESCE(STRING_AGG(\"ObjectType\" || ' ' || \"ActionType\", ', '), '') FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" LIKE '%{table}%'";
            var audit = cmd.ExecuteScalar()?.ToString();
            cmd.CommandText = $"SELECT oid FROM pg_policy WHERE polname = 'pol_{suffix}'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(before), $"the redeploy left the policy as it was (audit: {audit})");

            cmd.CommandText = $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{table}')";
            var extracted = cmd.ExecuteScalar()?.ToString() ?? "";
            Assert.That(extracted, withPublic ? Does.Contain("\"Roles\": \"public\"") : Does.Contain($"\\\"{role}\\\""),
                withPublic ? "the policy is PUBLIC's, as the server stores it" : "the extracted role keeps its quotes");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}; DROP ROLE IF EXISTS \"{role}\"";
            cmd.ExecuteNonQuery();
        }
    }

    // Quoted "AppUser" and unquoted AppUser (appuser) are two roles: a policy moved from one to the other changes.
    [Test]
    public void APolicyMovedToTheUnquotedRole_IsChanged()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"pol_move_{suffix}";
        var role = $"AppUser_{suffix}";
        string Json(string roles) => $$"""
            { "Schema": "public", "Name": "{{table}}", "RowLevelSecurity": true,
              "Columns": [ { "Name": "id", "DataType": "integer", "Nullable": false } ],
              "Indexes": [ { "Name": "pk_{{table}}", "PrimaryKey": true, "IndexColumns": "id" } ],
              "Policies": [ { "Name": "pol_{{suffix}}", "Command": "SELECT", "Roles": "{{roles}}", "UsingExpression": "id > 0" } ] }
            """;
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE ROLE \"{role}\"; CREATE ROLE {role.ToLowerInvariant()}";
        cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(cmd, Json($"\\\"{role}\\\""));
            RunTableQuenchProc(cmd, Json(role));
            cmd.CommandText = $"SELECT STRING_AGG(r.rolname, ',') FROM pg_policy p CROSS JOIN LATERAL unnest(p.polroles) AS pr(oid) JOIN pg_roles r ON r.oid = pr.oid WHERE p.polname = 'pol_{suffix}'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(role.ToLowerInvariant()));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS public.{table}; DROP ROLE IF EXISTS \"{role}\"; DROP ROLE IF EXISTS {role.ToLowerInvariant()}";
            cmd.ExecuteNonQuery();
        }
    }
}
