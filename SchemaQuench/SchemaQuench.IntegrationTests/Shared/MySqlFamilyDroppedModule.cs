// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>The MySQL-family DDL and grant probe for the dropped-module permission tests, shared by MySQL and MariaDB.</summary>
public static class MySqlFamilyDroppedModule
{
    public static string ProcedureScript(string body) =>
        $"DROP PROCEDURE IF EXISTS `p_perm`;\nDELIMITER //\nCREATE PROCEDURE `p_perm`() {body} //\nDELIMITER ;\n";

    public const string CreateGrantee = "CREATE USER IF NOT EXISTS 'perm_user'@'%' IDENTIFIED BY 'Perm-user-pw1!'";

    public static string GrantExecute(string db) => $"GRANT EXECUTE ON PROCEDURE `{db}`.`p_perm` TO 'perm_user'@'%'";

    public static string ExecuteGrantCount(string db) =>
        $"SELECT COUNT(*) FROM mysql.procs_priv WHERE Db = '{db}' AND User = 'perm_user' AND Routine_name = 'p_perm' AND FIND_IN_SET('Execute', Proc_priv) > 0";

    public static readonly string[] DropStatements = ["DROP PROCEDURE IF EXISTS `p_perm`"];

    public const string AuditedName = "p_perm";
}
