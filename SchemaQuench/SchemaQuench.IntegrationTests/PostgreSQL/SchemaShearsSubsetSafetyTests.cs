// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class SchemaShearsSubsetSafetyTests : SchemaShearsSubsetSafetyTestsSharedTests
{
    private static IConfigurationRoot Config => FactoryContainer.Resolve<IConfigurationRoot>();

    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => Config["ScriptTokens:MainDB"];
    protected override string TestConnectionString => ConnectionString.Build(Platform.PostgreSQL, Config["Target:Server"], "postgres",
        Config["Target:User"], Config["Target:Password"], Config["Target:Port"], ConnectionString.ReadProperties(Config, "Target:ConnectionProperties"));
    protected override string ProductPlatformFolder => "PostgreSQL";
    protected override string KeptTableManifestEntry => "Templates/Main/Tables/public.ShearsKeep.json";
    protected override string TableExistsSql(string tableName) =>
        $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{tableName}'";
    protected override string DropTablesSql => @"
DROP TABLE IF EXISTS public.""ShearsRemovable"";
DROP TABLE IF EXISTS public.""ShearsKeep"";";
}
