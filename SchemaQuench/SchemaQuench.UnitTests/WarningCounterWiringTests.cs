// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Reflection;
using log4net;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

// --ExitNonZeroOnWarning reads WarningCounter, which only sees what the tool logs if ConfigureLog4Net attaches it to
// the loggers the tool's own log configuration declares. Counting appended events directly proves the counter, not
// that it is wired to anything.
[NonParallelizable]
public class WarningCounterWiringTests
{
    [Test]
    public void ConfigureLog4Net_CountsAWarningTheToolLogs()
    {
        _ = typeof(ProductQuench).Assembly; // the tool assembly that embeds Log4Net.config
        var repository = LogManager.GetRepository(Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly());
        try
        {
            ConfigHelper.ConfigureLog4Net();
            WarningCounter.Reset();
            var progressLog = LogManager.GetLogger(repository.Name, "ProgressLog");

            progressLog.Info("not a warning");
            Assert.That(WarningCounter.Count, Is.Zero, "an informational line must not count");

            progressLog.Warn("a warning");
            Assert.That(WarningCounter.Count, Is.EqualTo(1), "a warning on the tool's progress log must be counted");
        }
        finally
        {
            repository.ResetConfiguration();
            WarningCounter.Reset();
        }
    }
}
