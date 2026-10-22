// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class DroppedModulePermissionTests : DroppedModulePermissionSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";

    protected override string ProcedureScript(string body) => MySqlFamilyDroppedModule.ProcedureScript(body);
    protected override string CreateGrantee => MySqlFamilyDroppedModule.CreateGrantee;
    protected override string GrantExecute => MySqlFamilyDroppedModule.GrantExecute(MainDb);
    protected override string ExecuteGrantCount => MySqlFamilyDroppedModule.ExecuteGrantCount(MainDb);
    protected override string[] DropStatements => MySqlFamilyDroppedModule.DropStatements;
    protected override string AuditedName => MySqlFamilyDroppedModule.AuditedName;

    // A role is a grantee with no host. Rendered 'perm_role'@'', the grant named a user that does not exist, and the
    // re-apply failed the deploy.
    [Test]
    public void ARedeployOfADroppedModule_PutsARolesGrantBack()
    {
        Exec("CREATE ROLE IF NOT EXISTS perm_role");
        try
        {
            Deploy();
            Exec($"GRANT EXECUTE ON PROCEDURE `{MainDb}`.`p_perm` TO perm_role");
            WriteProcedure("SELECT 2");

            var second = Deploy();

            Assert.That(Count($"SELECT COUNT(*) FROM mysql.procs_priv WHERE CONVERT(Db USING utf8mb4) COLLATE utf8mb4_general_ci = '{MainDb}' AND User = 'perm_role' AND Host = '' AND Routine_name = 'p_perm'"),
                Is.EqualTo(1), "the role holds EXECUTE again: " + second.Describe());
        }
        finally
        {
            Exec("DROP ROLE IF EXISTS perm_role");
        }
    }
}
