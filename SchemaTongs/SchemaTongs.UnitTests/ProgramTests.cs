// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class ProgramTests
{
    private IEnvironment _environment;
    private ILog _progressLog;
    private ILog _errorLog;

    [SetUp]
    public void SetUp()
    {
        _environment = Substitute.For<IEnvironment>();
        _progressLog = Substitute.For<ILog>();
        _errorLog = Substitute.For<ILog>();
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void UnhandledException_LogsExceptionAndExits()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            LogFactory.Register("ProgressLog", _progressLog);
            LogFactory.Register("ErrorLog", _errorLog);

            var exception = new Exception("Test Exception");
            var args = new UnhandledExceptionEventArgs(exception, false);

            Program.UnhandledException(this, args);

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Test Exception")));
            _errorLog.Received(1).Error(Arg.Any<object>());
            _environment.Received(1).Exit(3);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void UnhandledException_WithNestedExceptions_LogsAllDetails()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            LogFactory.Register("ProgressLog", _progressLog);
            LogFactory.Register("ErrorLog", _errorLog);

            var innerException = new InvalidOperationException("Inner error");
            var outerException = new Exception("Outer error", innerException);
            var args = new UnhandledExceptionEventArgs(outerException, false);

            Program.UnhandledException(this, args);

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Outer error")));
            _environment.Received(1).Exit(3);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_WithTargetPlatform_ReturnsPlatform()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>
            {
                ["Target:Platform"] = "SqlServer"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();

            Assert.That(result, Is.EqualTo(Platform.SqlServer));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_WithSourcePlatform_ReturnsPlatform()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>
            {
                ["Source:Platform"] = "PostgreSQL"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();

            Assert.That(result, Is.EqualTo(Platform.PostgreSQL));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_WithMSSQLAlias_ReturnsSqlServer()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>
            {
                ["Target:Platform"] = "MSSQL"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();

            Assert.That(result, Is.EqualTo(Platform.SqlServer));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_WithMySQL_ReturnsMySQL()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>
            {
                ["Target:Platform"] = "MySQL"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();

            Assert.That(result, Is.EqualTo(Platform.MySQL));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_NoPlatformSet_ThrowsException()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            Assert.Throws<RunFailedException>(() => Program.ResolvePlatform());

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_EmptyPlatform_ThrowsException()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>
            {
                ["Target:Platform"] = ""
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            Assert.Throws<RunFailedException>(() => Program.ResolvePlatform());

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_TargetTakesPrecedenceOverSource()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var configValues = new Dictionary<string, string>
            {
                ["Target:Platform"] = "MySQL",
                ["Source:Platform"] = "PostgreSQL"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();

            Assert.That(result, Is.EqualTo(Platform.MySQL));

            FactoryContainer.Clear();
        }
    }

    // --- WriteSchemasOnly ---

    [Test]
    public void WriteSchemasOnly_WithValidProduct_RegeneratesSchemaFiles()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            // Arrange: create a temp directory with a minimal Product.json
            var tempDir = Path.Combine(Path.GetTempPath(), $"SchemaTongs_WriteSchemasOnly_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "Product.json"),
                    """{"Name":"TestProduct","Platform":"SqlServer","ValidationScript":"SELECT 1","TemplateOrder":[]}""");

                var configValues = new Dictionary<string, string>
                {
                    ["Product:Path"] = tempDir
                };
                var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
                FactoryContainer.Register<IConfigurationRoot>(config);

                // Act
                Program.WriteSchemasOnly();

                // Assert: .json-schemas directory was created with platform-specific files
                var schemaDir = Path.Combine(tempDir, ".json-schemas");
                Assert.That(Directory.Exists(schemaDir), Is.True, ".json-schemas directory should be created");
                Assert.That(File.Exists(Path.Combine(schemaDir, "products.sqlserver.schema")), Is.True);
                Assert.That(File.Exists(Path.Combine(schemaDir, "tables.sqlserver.schema")), Is.True);
                Assert.That(File.Exists(Path.Combine(schemaDir, "templates.sqlserver.schema")), Is.True);
                Assert.That(File.Exists(Path.Combine(schemaDir, "indexedviews.sqlserver.schema")), Is.True);
            }
            finally
            {
                Directory.Delete(tempDir, true);
                FactoryContainer.Clear();
            }
        }
    }

    [Test]
    public void WriteSchemasOnly_PostgreSQL_GeneratesMaterializedViewSchema()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"SchemaTongs_WriteSchemasOnly_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "Product.json"),
                    """{"Name":"PgProduct","Platform":"PostgreSQL","ValidationScript":"SELECT 1","TemplateOrder":[]}""");

                var configValues = new Dictionary<string, string>
                {
                    ["Product:Path"] = tempDir
                };
                var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
                FactoryContainer.Register<IConfigurationRoot>(config);

                Program.WriteSchemasOnly();

                var schemaDir = Path.Combine(tempDir, ".json-schemas");
                Assert.That(File.Exists(Path.Combine(schemaDir, "materializedviews.postgresql.schema")), Is.True);
                Assert.That(File.Exists(Path.Combine(schemaDir, "tables.postgresql.schema")), Is.True);
                // PostgreSQL should NOT have indexedviews
                Assert.That(File.Exists(Path.Combine(schemaDir, "indexedviews.postgresql.schema")), Is.False);
            }
            finally
            {
                Directory.Delete(tempDir, true);
                FactoryContainer.Clear();
            }
        }
    }

    [Test]
    public void WriteSchemasOnly_MissingProductJson_ThrowsWithClearMessage()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"SchemaTongs_WriteSchemasOnly_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // No Product.json written
                var configValues = new Dictionary<string, string>
                {
                    ["Product:Path"] = tempDir
                };
                var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
                FactoryContainer.Register<IConfigurationRoot>(config);

                var ex = Assert.Throws<Exception>(() => Program.WriteSchemasOnly());
                Assert.That(ex.Message, Does.Contain("Product.json not found"));
            }
            finally
            {
                Directory.Delete(tempDir, true);
                FactoryContainer.Clear();
            }
        }
    }
}
