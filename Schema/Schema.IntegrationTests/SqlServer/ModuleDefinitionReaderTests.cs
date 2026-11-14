// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ModuleDefinitionReaderTests : ModuleDefinitionReaderSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string ConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string Setup => "CREATE TABLE dbo.mdr_t (id INT)\n--\nCREATE TRIGGER dbo.mdr_tr ON dbo.mdr_t AFTER INSERT AS BEGIN SET NOCOUNT ON; END";
    protected override string ChangeTrigger => "DISABLE TRIGGER dbo.mdr_tr ON dbo.mdr_t";
    protected override string Teardown => "DROP TABLE IF EXISTS dbo.mdr_t";
    protected override ModuleScriptIdentity Trigger => new() { Kind = ModuleKind.Trigger, Schema = "dbo", Name = "mdr_tr", TableSchema = "dbo", TableName = "mdr_t" };
}
