// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Parallelizable(scope: ParallelScope.Self)]
public class RebuildTableStorageTests : RebuildTableStorageSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    // ModifiedTableQuench only ever turns page compression on: an omitted PageCompressed reads as false, and turning
    // it off would rewrite a table nobody asked to change. A rebuild keeps it the same way.
    [Test]
    public void APageCompressedTable_StaysPageCompressed_WhenThePackageDoesNotDeclareIt()
    {
        using var r = Seeded("PAGE_COMPRESSED=1");

        Deploy(r, r.Json("VARCHAR(60)"));

        Assert.Multiple(() =>
        {
            Assert.That(r.Rebuilt(), Is.EqualTo("1"), "setup: the table was rebuilt");
            Assert.That(r.Options(), Does.Contain("page_compressed"));
        });
    }
}
