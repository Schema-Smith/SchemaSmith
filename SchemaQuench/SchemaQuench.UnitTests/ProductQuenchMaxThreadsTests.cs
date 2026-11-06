// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

// A MaxThreads outside 1-20 fell back to 10 with nothing logged, so a run asked for 32 threads used 10 and said nothing.
[TestFixture]
public class ProductQuenchMaxThreadsTests
{
    [TestCase("32", true)]
    [TestCase("0", true)]
    [TestCase("many", true)]
    [TestCase("20", false)]
    [TestCase(null, false)]
    public void AMaxThreadsOutsideItsRange_IsReportedWhenItFallsBack(string value, bool warned)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            var log = Substitute.For<ILog>();
            try
            {
                const string packagePath = "Product";
                var productPath = System.IO.Path.Join(packagePath, "Product.json");
                var file = Substitute.For<IFile>();
                var directory = Substitute.For<IDirectory>();
                directory.Exists(packagePath).Returns(true);
                file.Exists(productPath).Returns(true);
                file.ReadAllText(productPath).Returns("""{ "Name": "ThreadsProduct", "Platform": "SqlServer", "ScriptFolders": [] }""");
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = packagePath,
                    ["Target:Server"] = "primary-server",
                    ["MaxThreads"] = value
                }).Build());
                FactoryContainer.Register(file);
                FactoryContainer.Register(directory);
                LogFactory.Register("ProgressLog", log);

                _ = new ProductQuench();

                if (warned)
                    log.Received().Warn(Arg.Is<object>(m => m.ToString()!.Contains("MaxThreads") && m.ToString()!.Contains("10")));
                else
                    log.DidNotReceive().Warn(Arg.Is<object>(m => m.ToString()!.Contains("MaxThreads")));
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }
}
