// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

// A package the user can fix is reported in one line with exit 2; a fault in the tool must not read as one, or the
// user is sent to fix a package that is fine. LINQ's "Sequence contains no elements" is an InvalidOperationException too.
[TestFixture]
public class PackageLoadClassificationTests
{
    [Test]
    public void APackageError_IsReportedAsAFailedRun()
    {
        var e = Assert.Throws<RunFailedException>(() => ProductQuench.LoadPackage<object>(() => throw new PackageException("Template 'Main' is a schema template on MySQL.")));

        Assert.That(e!.Message, Is.EqualTo("Template 'Main' is a schema template on MySQL."));
    }

    [Test]
    public void AFaultInTheTool_IsNotReportedAsAPackageError()
    {
        Assert.Throws<InvalidOperationException>(() => ProductQuench.LoadPackage(() => Array.Empty<int>().First()));
    }
}
