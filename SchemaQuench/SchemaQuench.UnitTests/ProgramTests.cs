// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using log4net;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class ProgramTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void ShouldHandleUnhandledException()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            var exception = new Exception("Test Exception");
            Program.UnhandledException("TestApp", new UnhandledExceptionEventArgs(exception, false));

            _progressLog.Received(1).Error("EXCEPTION - See the error log:\r\nSystem.Exception: Test Exception");
            _errorLog.Received(1).Error(exception);
            _environment.Received(1).Exit(3);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    // The switch form was accepted (it is in KnownArguments) and ignored: only the bare positional word skipped kindling.
    [TestCase("SchemaQuench.exe SkipKindlingForge", true)]
    [TestCase("SchemaQuench.exe --SkipKindlingForge", true)]
    [TestCase("SchemaQuench.exe --WhatIf", false)]
    public void SkipKindlingForge_IsReadInEitherForm(string commandLine, bool skips)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(_environment);
            _environment.CommandLine.Returns(commandLine);
            try
            {
                Assert.That(Program.SkipsKindling(commandLine.Split(' ')[1..]), Is.EqualTo(skips));
            }
            finally
            {
                FactoryContainer.Clear();
            }
        }
    }
}
