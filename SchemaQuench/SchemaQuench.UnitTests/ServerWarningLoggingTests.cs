// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Reflection;
using log4net;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

// A message a SchemaSmith routine raises with a WARNING: prefix names a declared change the deploy did not apply.
// It has to reach the progress log as a warning, or --ExitNonZeroOnWarning cannot see it.
[NonParallelizable]
public class ServerWarningLoggingTests
{
    [TestCase("  WARNING: Kept dbo.T.IX as it is: foreign key dbo.C.FK references it", true)]
    [TestCase("WARNING: the newest CDC capture instance on dbo.T does not capture the table's current columns", true)]
    [TestCase("  Kept dbo.T.IX as it is: foreign key dbo.C.FK references it", false)]
    [TestCase("  Adding new table dbo.T", false)]
    [TestCase("  A warning: in the middle is not the convention", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsServerWarning_RecognisesTheWarningPrefixOnly(string message, bool expected)
        => Assert.That(DatabaseQuench.IsServerWarning(message), Is.EqualTo(expected));

    [Test]
    public void LogServerMessage_CountsAWarningAndNotProgress()
    {
        var repository = LogManager.GetRepository(Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly());
        try
        {
            ConfigHelper.ConfigureLog4Net();
            WarningCounter.Reset();
            var quench = new DatabaseQuench("server", new Product { Name = "P", Platform = Platform.SqlServer }, new Template { Name = "T" }, "db",
                false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

            quench.LogServerMessage("      Kept dbo.T.IX as it is");
            Assert.That(WarningCounter.Count, Is.Zero, "a progress line must not count");

            quench.LogServerMessage("      WARNING: Kept dbo.T.IX as it is: foreign key dbo.C.FK references it");
            Assert.That(WarningCounter.Count, Is.EqualTo(1), "a WARNING: line from the server must be logged as a warning");
        }
        finally
        {
            repository.ResetConfiguration();
            WarningCounter.Reset();
        }
    }
}
