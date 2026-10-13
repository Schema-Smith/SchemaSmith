// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class LogBackupTests
{
    private IEnvironment _mockEnvironment;
    private IDirectory _mockDirectory;
    private IFile _mockFile;

    [SetUp]
    public void SetUp()
    {
        _mockEnvironment = Substitute.For<IEnvironment>();
        _mockDirectory = Substitute.For<IDirectory>();
        _mockFile = Substitute.For<IFile>();

        FactoryContainer.Register<IEnvironment>(_mockEnvironment);
        FactoryContainer.Register<IDirectory>(_mockDirectory);
        FactoryContainer.Register<IFile>(_mockFile);

        // Set default command line (no LogPath switch)
        _mockEnvironment.CommandLine.Returns("app.exe");

        LogFactory.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    // A failure the user can fix ends in one plain line and exit 2. It is not an unhandled exception (exit 3), which is
    // what every such failure used to produce: a stack dump the user had to read past to find the message.
    [Test]
    public void FailedRunExit_LogsTheMessage_AndExitsTwo()
    {
        var progressLog = Substitute.For<ILog>();
        var errorLog = Substitute.For<ILog>();
        LogFactory.Register("ProgressLog", progressLog);
        LogFactory.Register("ErrorLog", errorLog);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(Array.Empty<string>());
        var failure = new RunFailedException("Platform is required.");

        LogBackup.FailedRunExit("TestApp", failure);

        Assert.Multiple(() =>
        {
            progressLog.Received(1).Error("FAILED: Platform is required.");
            errorLog.Received(1).Error("Platform is required.", failure);
            _mockEnvironment.Received(1).Exit(2);
            _mockEnvironment.DidNotReceive().Exit(3);
        });
    }

    // --ExitNonZeroOnWarning / ExitNonZeroOnWarning: a run that would exit 0 but logged a warning exits 1. Off by default,
    // and it never lowers or replaces a failure code.
    [TestCase(null, true, 0, 0)]
    [TestCase("true", false, 0, 0)]
    [TestCase("true", true, 0, 1)]
    [TestCase("TRUE", true, 0, 1)]
    [TestCase("true", true, 2, 2)]
    [TestCase("false", true, 0, 0)]
    public void BackupLogsAndExit_ExitNonZeroOnWarning(string setting, bool warned, int runCode, int expected)
    {
        var values = new System.Collections.Generic.Dictionary<string, string>();
        if (setting != null) values["ExitNonZeroOnWarning"] = setting;
        FactoryContainer.Register<Microsoft.Extensions.Configuration.IConfigurationRoot>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build());
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(Array.Empty<string>());
        lock (FactoryContainer.SharedLockObject)
        {
            WarningCounter.Reset();
            try
            {
                if (warned)
                    new WarningCounter().DoAppend(new log4net.Core.LoggingEvent(
                        new log4net.Core.LoggingEventData { Level = log4net.Core.Level.Warn, Message = "a warning" }));

                LogBackup.BackupLogsAndExit("TestApp", runCode);

                _mockEnvironment.Received(1).Exit(expected);
            }
            finally { WarningCounter.Reset(); }
        }
    }

    [Test]
    public void BackupLogsAndExit_ExitNonZeroOnWarning_AsABareSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --ExitNonZeroOnWarning");
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(Array.Empty<string>());
        lock (FactoryContainer.SharedLockObject)
        {
            WarningCounter.Reset();
            try
            {
                new WarningCounter().DoAppend(new log4net.Core.LoggingEvent(
                    new log4net.Core.LoggingEventData { Level = log4net.Core.Level.Error, Message = "worse than a warning" }));

                LogBackup.BackupLogsAndExit("TestApp");

                _mockEnvironment.Received(1).Exit(1);
            }
            finally { WarningCounter.Reset(); }
        }
    }

    [Test]
    public void BackupLogsAndExit_CreatesBackupDirectory()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        LogBackup.BackupLogsAndExit("TestApp");

        _mockDirectory.Received().CreateDirectory(Arg.Is<string>(s => s.Contains("TestApp.0001")));
    }

    // Two runs from one install both pass the Exists check and land on the same directory --
    // CreateDirectory is idempotent, so nothing stops them. The loser's Copy then hit an existing
    // destination, threw, and the catch exited 4: a run that had just reported success was recorded as a
    // failure. It must take the next directory and keep its own exit code.
    [Test]
    public void BackupLogsAndExit_AnotherRunAlreadyClaimedTheDirectory_TakesTheNextOneAndKeepsItsExitCode()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { Path.Join("logs", "TestApp - run.log") });
        // The run that won the race already wrote this file, so the copy into 0001 fails.
        _mockFile.When(f => f.Copy(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("TestApp.0001")),
                                  Arg.Any<bool>()))
                 .Do(_ => throw new IOException("file exists"));

        LogBackup.BackupLogsAndExit("TestApp");

        Assert.Multiple(() =>
        {
            _mockFile.Received().Copy(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("TestApp.0002")),
                Arg.Any<bool>());
            _mockEnvironment.Received().Exit(0);
            _mockEnvironment.DidNotReceive().Exit(4);
        });
    }

    // Even when no directory can be claimed at all, archiving logs is a convenience: it must not replace
    // the result the run actually reached.
    [Test]
    public void BackupLogsAndExit_CannotBackUpAtAll_StillReportsTheRunsOwnExitCode()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { Path.Join("logs", "TestApp - run.log") });
        _mockFile.When(f => f.Copy(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()))
                 .Do(_ => throw new IOException("every destination is taken"));

        LogBackup.BackupLogsAndExit("TestApp", 2);

        Assert.Multiple(() =>
        {
            _mockEnvironment.Received().Exit(2);
            _mockEnvironment.DidNotReceive().Exit(4);
        });
    }

    // Directories earlier runs left behind are not collisions. Counting them against the retry limit meant that once
    // fifty backups existed, no run archived its logs again.
    [Test]
    public void BackupLogsAndExit_ManyEarlierBackups_StillArchivesIntoTheNextFreeDirectory()
    {
        _mockDirectory.Exists(Arg.Is<string>(s => IsEarlierBackup(s))).Returns(true);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { Path.Join("logs", "TestApp - run.log") });

        LogBackup.BackupLogsAndExit("TestApp");

        _mockFile.Received().Copy(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("TestApp.0061")), Arg.Any<bool>());
    }

    private static bool IsEarlierBackup(string path) => ExistingBackupIndex(path) is >= 1 and <= 60;

    private static int ExistingBackupIndex(string path)
    {
        var dot = path.LastIndexOf("TestApp.", StringComparison.Ordinal);
        return dot >= 0 && int.TryParse(path.AsSpan(dot + "TestApp.".Length), out var index) ? index : 0;
    }

    [Test]
    public void BackupLogsAndExit_IncrementsDirectoryWhenExists()
    {
        _mockDirectory.Exists(Arg.Is<string>(s => s.Contains("0001"))).Returns(true);
        _mockDirectory.Exists(Arg.Is<string>(s => s.Contains("0002"))).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        LogBackup.BackupLogsAndExit("TestApp");

        _mockDirectory.Received().CreateDirectory(Arg.Is<string>(s => s.Contains("TestApp.0002")));
    }

    [Test]
    public void BackupLogsAndExit_CopiesLogFiles()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), "TestApp - *.log", SearchOption.TopDirectoryOnly)
            .Returns(new[] { "/logs/TestApp - Progress.log", "/logs/TestApp - Error.log" });

        LogBackup.BackupLogsAndExit("TestApp");

        _mockFile.Received(2).Copy(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public void BackupLogsAndExit_CopiesSummaryFiles()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), "TestApp - *.log", SearchOption.TopDirectoryOnly)
            .Returns(Array.Empty<string>());
        _mockDirectory.GetFiles(Arg.Any<string>(), "TestApp - Summary.*", SearchOption.TopDirectoryOnly)
            .Returns(new[] { "/logs/TestApp - Summary.json", "/logs/TestApp - Summary.md" });

        LogBackup.BackupLogsAndExit("TestApp");

        _mockFile.Received(1).Copy(Arg.Is<string>(s => s.EndsWith("Summary.json")), Arg.Any<string>(), Arg.Any<bool>());
        _mockFile.Received(1).Copy(Arg.Is<string>(s => s.EndsWith("Summary.md")), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public void BackupLogsAndExit_ExitsWithSpecifiedCode()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        LogBackup.BackupLogsAndExit("TestApp", 2);

        _mockEnvironment.Received().Exit(2);
    }

    [Test]
    public void BackupLogsAndExit_ExitsWithZeroByDefault()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        LogBackup.BackupLogsAndExit("TestApp");

        _mockEnvironment.Received().Exit(0);
    }

    [Test]
    public void BackupLogsAndExit_ExitsWithCode4OnException()
    {
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.CreateDirectory(Arg.Any<string>()).Returns(_ => throw new Exception("disk full"));

        LogBackup.BackupLogsAndExit("TestApp");

        _mockEnvironment.Received().Exit(4);
    }

    [Test]
    public void BackupLogsAndExit_UsesLogPathSwitch()
    {
        var expectedBase = Path.GetFullPath("/custom/logs").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath:/custom/logs");
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        LogBackup.BackupLogsAndExit("TestApp");

        _mockDirectory.Received().CreateDirectory(Arg.Is<string>(s =>
            s.Contains(expectedBase) && s.Contains("TestApp.0001")));
    }

    [Test]
    public void BackupLogsAndExit_UsesResolvedLogPath()
    {
        var expectedBase = Path.GetFullPath("relbackup").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath:relbackup");
        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(Array.Empty<string>());

        LogBackup.BackupLogsAndExit("TestApp");

        _mockDirectory.Received().CreateDirectory(Arg.Is<string>(s =>
            s == Path.Join(expectedBase, "TestApp.0001")));
        _mockDirectory.Received().GetFiles(expectedBase, "TestApp - *.log", SearchOption.TopDirectoryOnly);
    }

    [Test]
    public void UnhandledExceptionLogger_LogsToProgressAndErrorLogs()
    {
        var mockProgressLog = Substitute.For<ILog>();
        var mockErrorLog = Substitute.For<ILog>();
        LogFactory.Register("ProgressLog", mockProgressLog);
        LogFactory.Register("ErrorLog", mockErrorLog);

        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        var exception = new Exception("test error");
        var args = new UnhandledExceptionEventArgs(exception, false);

        LogBackup.UnhandledExceptionLogger("TestApp", args);

        mockProgressLog.Received().Error(Arg.Is<string>(s => s.Contains("EXCEPTION")));
        mockErrorLog.Received().Error(exception);
    }

    [Test]
    public void UnhandledExceptionLogger_ExitsWithCode3()
    {
        var mockProgressLog = Substitute.For<ILog>();
        var mockErrorLog = Substitute.For<ILog>();
        LogFactory.Register("ProgressLog", mockProgressLog);
        LogFactory.Register("ErrorLog", mockErrorLog);

        _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
        _mockDirectory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(Array.Empty<string>());

        var args = new UnhandledExceptionEventArgs(new Exception("test"), false);

        LogBackup.UnhandledExceptionLogger("TestApp", args);

        _mockEnvironment.Received().Exit(3);
    }
}
