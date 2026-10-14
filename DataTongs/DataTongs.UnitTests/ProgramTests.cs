// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.UnitTests;

[TestFixture]
public class ProgramTests
{
    private ILog _errorLog;
    private ILog _progressLog;
    private IEnvironment _environment;

    [SetUp]
    public void SetUp()
    {
        _errorLog = Substitute.For<ILog>();
        _progressLog = Substitute.For<ILog>();
        _environment = Substitute.For<IEnvironment>();
    }

    [Test]
    public void UnhandledException_ShouldLogErrorAndExit()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            var exception = new Exception("Test Exception");
            Program.UnhandledException(this, new UnhandledExceptionEventArgs(exception, false));

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Test Exception")));
            _errorLog.Received(1).Error(exception);
            _environment.Received(1).Exit(3);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void UnhandledException_WithNestedExceptionMessage_ShouldLogFullMessage()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            var innerException = new InvalidOperationException("Inner error");
            var exception = new Exception("Outer error", innerException);
            Program.UnhandledException(this, new UnhandledExceptionEventArgs(exception, false));

            _progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Outer error")));
            _errorLog.Received(1).Error(exception);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void UnhandledException_WithTerminatingTrue_ShouldStillExit()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            var exception = new Exception("Terminating exception");
            Program.UnhandledException(this, new UnhandledExceptionEventArgs(exception, true));

            _environment.Received(1).Exit(3);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_SqlServer_ReturnsSqlServer()
    {
        var configValues = new Dictionary<string, string>
        {
            ["Source:Platform"] = "SqlServer"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();
            Assert.That(result, Is.EqualTo(Platform.SqlServer));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_PostgreSQL_ReturnsPostgreSQL()
    {
        var configValues = new Dictionary<string, string>
        {
            ["Source:Platform"] = "PostgreSQL"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();
            Assert.That(result, Is.EqualTo(Platform.PostgreSQL));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_MySQL_ReturnsMySQL()
    {
        var configValues = new Dictionary<string, string>
        {
            ["Source:Platform"] = "MySQL"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();
            Assert.That(result, Is.EqualTo(Platform.MySQL));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_FallsBackToTargetPlatform()
    {
        var configValues = new Dictionary<string, string>
        {
            ["Target:Platform"] = "MySQL"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var result = Program.ResolvePlatform();
            Assert.That(result, Is.EqualTo(Platform.MySQL));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ResolvePlatform_NoPlatform_ThrowsException()
    {
        var configValues = new Dictionary<string, string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var ex = Assert.Throws<RunFailedException>(() => Program.ResolvePlatform());
            Assert.That(ex.Message, Does.Contain("Platform is required"));

            FactoryContainer.Clear();
        }
    }
}
