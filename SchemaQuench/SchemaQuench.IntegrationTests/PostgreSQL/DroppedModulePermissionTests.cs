// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class DroppedModulePermissionTests : DroppedModulePermissionSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";

    private const string ProcAcl = "SELECT (aclexplode(p.proacl)).* FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname = 'p_perm'";

    protected override string ProcedureScript(string body) =>
        $"DROP PROCEDURE IF EXISTS public.p_perm();\nCREATE PROCEDURE public.p_perm() LANGUAGE sql AS $$ {body} $$;";
    protected override string CreateGrantee =>
        "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'perm_role') THEN CREATE ROLE perm_role NOLOGIN; END IF; END $$";
    // A procedure is executable by PUBLIC until that is revoked, so the grant under test is the pair: PUBLIC out, the role in.
    protected override string GrantExecute => "REVOKE EXECUTE ON PROCEDURE public.p_perm() FROM PUBLIC; GRANT EXECUTE ON PROCEDURE public.p_perm() TO perm_role";
    protected override string ExecuteGrantCount =>
        $"SELECT COUNT(*) FROM ({ProcAcl}) a WHERE a.privilege_type = 'EXECUTE' AND a.grantee = (SELECT oid FROM pg_roles WHERE rolname = 'perm_role') " +
        $"AND NOT EXISTS (SELECT 1 FROM ({ProcAcl}) b WHERE b.grantee = 0)";
    protected override string[] DropStatements => ["DROP PROCEDURE IF EXISTS public.p_perm()"];
    protected override string AuditedName => "public.p_perm";
}
