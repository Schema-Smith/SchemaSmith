// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using Microsoft.Extensions.Configuration;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[TestFixture]
[Category("PostgreSQL")]
[NonParallelizable]
public class PreFlight_TestConnectionTests : BaseTableQuenchTests
{
    [Test]
    public void TestConnection_ValidServer_Succeeds()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "ValidProduct");
            try
            {
                var pq = new ProductQuench();
                Assert.That(pq.RunPreFlight(previewTargets: false), Is.True);
                Assert.That(pq.Failed, Is.False);
            }
            finally
            {
                config["SchemaPackagePath"] = null;
            }
        }
    }

    [Test]
    public void TestConnection_BadCredentials_FailsWithoutThrowing()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath("PostgreSQL", "ValidProduct");
            var originalPassword = config["Target:Password"];
            config["Target:Password"] = "WrongPassword_xK9z!";
            var progressErrors = new List<string>();
            var failureLines = new List<string>();
            var progressLog = Substitute.For<log4net.ILog>();
            progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => progressErrors.Add(ci.Arg<object>()?.ToString() ?? ""));
            var failureLog = Substitute.For<log4net.ILog>();
            failureLog.When(l => l.Info(Arg.Any<object>())).Do(ci => failureLines.Add(ci.Arg<object>()?.ToString() ?? ""));
            LogFactory.Register("ProgressLog", progressLog);
            LogFactory.Register("FailureLog", failureLog);
            try
            {
                var pq = new ProductQuench();
                Assert.That(pq.RunPreFlight(previewTargets: false), Is.False);
                Assert.That(pq.Failed, Is.True);

                // The driver's reason belongs on the line and in a Failures.log block, not only in Errors.log.
                var failedLine = progressErrors.FirstOrDefault(m => m.Contains("**CONNECTION FAILED**"));
                Assert.That(failedLine, Is.Not.Null, "the server check reports the failed connection");
                Assert.Multiple(() =>
                {
                    Assert.That(failedLine, Does.Match(@"with user \S+: \S.* \*\*CONNECTION FAILED\*\*"), "the driver's reason is on the line");
                    Assert.That(string.Join("\n", failureLines), Does.Contain("FAILED  [Validate]"), "and Failures.log has a block for it");
                });
            }
            finally
            {
                LogFactory.Clear();
                config["Target:Password"] = originalPassword;
                config["SchemaPackagePath"] = null;
            }
        }
    }
}
