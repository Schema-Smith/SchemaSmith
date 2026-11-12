// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class SchemaShearsSubsetSafetyTests : SchemaShearsSubsetSafetyTestsSharedTests
{
    private static IConfigurationRoot Config => FactoryContainer.Resolve<IConfigurationRoot>();

    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => Config["ScriptTokens:MainDB"];
    protected override string TestConnectionString => ConnectionString.Build(Platform.SqlServer, Config["Target:Server"], "master",
        Config["Target:User"], Config["Target:Password"], Config["Target:Port"], ConnectionString.ReadProperties(Config, "Target:ConnectionProperties"));
    protected override string ProductPlatformFolder => "SqlServer";
    protected override string KeptTableManifestEntry => "Templates/Main/Tables/dbo.ShearsKeep.json";
    protected override string CompletedCountSql(string scriptPath) =>
        $"SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts WHERE ProductName = 'ShearsSource' AND ScriptPath = '{scriptPath}'";
    protected override string TableExistsSql(string tableName) => $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
    protected override string DropTablesSql => @"
DROP TABLE IF EXISTS [dbo].[ShearsRemovable];
DROP TABLE IF EXISTS [dbo].[ShearsKeep];";
}
