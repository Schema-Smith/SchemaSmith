// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class UnchangedModuleSkipTests : UnchangedModuleSkipSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";
    protected override string TableJson => MySqlFamilyUnchangedSkip.TableJson;
    protected override Dictionary<string, string> Scripts => MySqlFamilyUnchangedSkip.Scripts;
    protected override string AlterViewByHand => MySqlFamilyUnchangedSkip.AlterViewByHand;
    protected override string ChangedFunctionScript => MySqlFamilyUnchangedSkip.ChangedFunctionScript;
    protected override string[] DropStatements => MySqlFamilyUnchangedSkip.DropStatements;
}
