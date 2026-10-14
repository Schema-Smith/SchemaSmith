// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.UnitTests
{
    [TestFixture]
    public class ProductQuenchMinimumVersionTests
    {
        [Test]
        public void BuildMinimumVersionFailures_FlagsBelowFloorServers_Only()
        {
            var detected = new List<(string, TargetVersionInfo)>
            {
                ("primary", new TargetVersionInfo(Platform.PostgreSQL, "150010", 15)),
                ("secondary", new TargetVersionInfo(Platform.PostgreSQL, "160004", 16))
            };

            var failures = ProductQuench.BuildMinimumVersionFailures(detected, requiredComparable: 16, declaredMinimum: "16");

            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures.Single(), Does.Contain("primary"));
            // Detected version is displayed normalized to its major, not the raw server_version_num (150010).
            Assert.That(failures.Single(), Does.Contain("version 15"));
            Assert.That(failures.Single(), Does.Not.Contain("150010"));
            Assert.That(failures.Single(), Does.Contain("16"));
        }

        [Test]
        public void BuildMinimumVersionFailures_Empty_WhenAllMeetFloor()
        {
            var detected = new List<(string, TargetVersionInfo)>
            {
                ("primary", new TargetVersionInfo(Platform.SqlServer, "16", 16))
            };

            var failures = ProductQuench.BuildMinimumVersionFailures(detected, requiredComparable: 15, declaredMinimum: "2019");

            Assert.That(failures, Is.Empty);
        }

        private static void ConfigureProduct(string platform, string minimumVersion, string secondaryServers = "")
        {
            const string schemaPackagePath = "Product";
            var productPath = System.IO.Path.Combine(schemaPackagePath, "Product.json");
            var file = Substitute.For<IFile>();
            var directory = Substitute.For<IDirectory>();
            file.Exists(schemaPackagePath).Returns(false);
            directory.Exists(schemaPackagePath).Returns(true);
            file.Exists(productPath).Returns(true);
            file.ReadAllText(productPath).Returns(
                $$"""
                  {
                    "Name": "VersionGateProduct",
                    "Platform": "{{platform}}",
                    "MinimumVersion": "{{minimumVersion}}",
                    "ScriptFolders": []
                  }
                  """);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = schemaPackagePath,
                    ["Target:Server"] = "primary-server",
                    ["Target:SecondaryServers"] = secondaryServers,
                    ["MaxThreads"] = "1"
                })
                .Build();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IFile>(file);
            FactoryContainer.Register<IDirectory>(directory);
        }

        private sealed class StubDetectProductQuench : ProductQuench
        {
            private readonly string _rawVersion;
            public StubDetectProductQuench(string rawVersion) { _rawVersion = rawVersion; }

            internal override IDbCommand GetCommand(string server)
            {
                var command = Substitute.For<IDbCommand>();
                command.Connection.Returns(Substitute.For<IDbConnection>());
                command.ExecuteScalar().Returns(_rawVersion);
                return command;
            }
        }

        [Test]
        public void ValidateMinimumVersion_Throws_WhenDetectedBelowFloor()
        {
            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Clear();
                try
                {
                    ConfigureProduct("PostgreSQL", minimumVersion: "16");
                    var quench = new StubDetectProductQuench("150010"); // server_version_num -> major 15

                    var ex = Assert.Throws<RunFailedException>(() => quench.ValidateMinimumVersion());
                    Assert.That(ex!.Message, Does.Contain("below the product's declared MinimumVersion"));
                    Assert.That(ex.Message, Does.Contain("primary-server"));
                }
                finally { FactoryContainer.Clear(); }
            }
        }

        [Test]
        public void ValidateMinimumVersion_DoesNotThrow_WhenDetectedMeetsFloor()
        {
            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Clear();
                try
                {
                    ConfigureProduct("PostgreSQL", minimumVersion: "15");
                    var quench = new StubDetectProductQuench("150010"); // major 15 meets declared min 15

                    Assert.DoesNotThrow(() => quench.ValidateMinimumVersion());
                }
                finally { FactoryContainer.Clear(); }
            }
        }

        [Test]
        public void ValidateMinimumVersion_ChecksPrimaryAndSecondaryServers()
        {
            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Clear();
                try
                {
                    ConfigureProduct("SqlServer", minimumVersion: "2019", secondaryServers: "secondary-server");
                    var quench = new StubDetectProductQuench("14"); // major 14 < 2019 (major 15)

                    var ex = Assert.Throws<RunFailedException>(() => quench.ValidateMinimumVersion());
                    Assert.That(ex!.Message, Does.Contain("primary-server"));
                    Assert.That(ex.Message, Does.Contain("secondary-server"));
                }
                finally { FactoryContainer.Clear(); }
            }
        }

        [Test]
        public void ValidateServerVersionFloor_Throws_WhenBelowIntrinsicFloor_NoMinimumVersionDeclared()
        {
            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Clear();
                try
                {
                    ConfigureProduct("SqlServer", minimumVersion: "");   // no declared floor at all
                    var quench = new StubDetectProductQuench("9");         // SQL Server 2005, below floor 10

                    var ex = Assert.Throws<RunFailedException>(() => quench.ValidateServerVersionFloor());
                    Assert.That(ex!.Message, Does.Contain("below the minimum supported"));
                    Assert.That(ex.Message, Does.Contain("primary-server"));
                }
                finally { FactoryContainer.Clear(); }
            }
        }

        [Test]
        public void ValidateServerVersionFloor_DoesNotThrow_WhenAtFloor()
        {
            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Clear();
                try
                {
                    ConfigureProduct("SqlServer", minimumVersion: "");
                    var quench = new StubDetectProductQuench("10");   // SQL Server 2008 == floor

                    Assert.DoesNotThrow(() => quench.ValidateServerVersionFloor());
                }
                finally { FactoryContainer.Clear(); }
            }
        }

        [Test]
        public void ValidateMinimumVersion_Throws_WhenMinimumVersionUnparseable()
        {
            lock (FactoryContainer.SharedLockObject)
            {
                FactoryContainer.Clear();
                try
                {
                    ConfigureProduct("PostgreSQL", minimumVersion: "not-a-version");
                    var quench = new StubDetectProductQuench("150010");

                    var ex = Assert.Throws<RunFailedException>(() => quench.ValidateMinimumVersion());
                    Assert.That(ex!.Message, Does.Contain("is not a valid"));
                }
                finally { FactoryContainer.Clear(); }
            }
        }
    }
}
