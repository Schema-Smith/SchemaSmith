// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class DroppedModulePermissionTests : DroppedModulePermissionSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";

    protected override string ProcedureScript(string body) =>
        $"SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nIF OBJECT_ID(N'[dbo].[p_perm]') IS NOT NULL DROP PROCEDURE [dbo].[p_perm]\r\nGO\r\n\r\nCREATE PROCEDURE [dbo].[p_perm] AS {body}\r\nGO\r\n";
    protected override string CreateGrantee => "IF USER_ID('perm_user') IS NULL CREATE USER perm_user WITHOUT LOGIN";
    protected override string GrantExecute => "GRANT EXECUTE ON [dbo].[p_perm] TO perm_user";
    protected override string ExecuteGrantCount =>
        "SELECT COUNT(*) FROM sys.database_permissions WHERE class = 1 AND major_id = OBJECT_ID('[dbo].[p_perm]') AND grantee_principal_id = USER_ID('perm_user') AND permission_name = 'EXECUTE' AND state = 'G'";
    protected override string[] DropStatements => ["DROP PROCEDURE IF EXISTS [dbo].[p_perm]"];
    protected override string AuditedName => "[dbo].[p_perm]";
}
