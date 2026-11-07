// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.Shared;
using Schema.Utility;

namespace Schema.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class ModuleDefinitionReaderTests : ModuleDefinitionReaderSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string Setup => "CREATE TABLE mdr_t (id INT)\n--\nCREATE TRIGGER mdr_tr BEFORE INSERT ON mdr_t FOR EACH ROW SET NEW.id = NEW.id";
    protected override string ChangeTrigger => "DROP TRIGGER mdr_tr\n--\nCREATE TRIGGER mdr_tr BEFORE INSERT ON mdr_t FOR EACH ROW SET NEW.id = NEW.id + 1";
    protected override string Teardown => "DROP TABLE IF EXISTS mdr_t";
    protected override ModuleScriptIdentity Trigger => new() { Kind = ModuleKind.Trigger, Name = "mdr_tr", TableName = "mdr_t" };
}
