// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;

namespace SchemaQuench.UnitTests;

/// <summary>
/// A run that finished but left a declared feature unapplied, because the target lacks it, is incomplete and exits 1.
/// It used to exit 0, so automation could not tell a downgraded deploy from a complete one.
/// </summary>
[TestFixture]
public class ProductQuenchIncompleteTests
{
    private static ProductQuench NewQuench()
    {
        const string packagePath = "Product";
        var productPath = System.IO.Path.Join(packagePath, "Product.json");
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        directory.Exists(packagePath).Returns(true);
        file.Exists(productPath).Returns(true);
        file.ReadAllText(productPath).Returns("""{ "Name": "IncompleteProduct", "Platform": "SqlServer", "ScriptFolders": [] }""");
        FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["SchemaPackagePath"] = packagePath,
            ["Target:Server"] = "primary-server",
            ["MaxThreads"] = "1"
        }).Build());
        FactoryContainer.Register(file);
        FactoryContainer.Register(directory);
        return new ProductQuench();
    }

    [TestCase(null, false)]
    [TestCase("created", false)]
    [TestCase("dropSuppressed", false)]
    [TestCase("downgraded", true)]
    public void Incomplete_IsTrueOnlyWhenSomethingWasDowngraded(string action, bool expected)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            try
            {
                var quench = NewQuench();
                if (action != null)
                    quench.ChangeAudit.Record("data delivery", "dbo.Countries", action);

                Assert.That(quench.Incomplete, Is.EqualTo(expected));
            }
            finally { FactoryContainer.Clear(); }
        }
    }
}
