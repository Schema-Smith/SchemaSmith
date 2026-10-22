// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class DroppedModulePermissionTests : DroppedModulePermissionSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";

    protected override string ProcedureScript(string body) => MySqlFamilyDroppedModule.ProcedureScript(body);
    protected override string CreateGrantee => MySqlFamilyDroppedModule.CreateGrantee;
    protected override string GrantExecute => MySqlFamilyDroppedModule.GrantExecute(MainDb);
    protected override string ExecuteGrantCount => MySqlFamilyDroppedModule.ExecuteGrantCount(MainDb);
    protected override string[] DropStatements => MySqlFamilyDroppedModule.DropStatements;
    protected override string AuditedName => MySqlFamilyDroppedModule.AuditedName;
}
