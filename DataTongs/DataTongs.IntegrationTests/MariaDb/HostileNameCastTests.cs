// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using DataTongs.IntegrationTests.Shared;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;

namespace DataTongs.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class HostileNameCastTests : HostileNameCastSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";
    protected override string Database => FixtureSetup.MainDb;
    protected override string DatabaseConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string Schema => "";
    protected override string H => "a'b%c`d.e\\f,g h éΩ";
    protected override string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    protected override IEnumerable<string> Batches(string script) =>
        script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries).Where(b => !string.IsNullOrWhiteSpace(b));
}
