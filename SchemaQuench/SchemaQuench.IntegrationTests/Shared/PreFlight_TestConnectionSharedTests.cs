// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using Microsoft.Extensions.Configuration;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

public abstract class PreFlight_TestConnectionSharedTests : BaseTableQuenchTests
{
    protected abstract string ProductPlatformFolder { get; }

    [Test]
    public void TestConnection_ValidServer_Succeeds()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ValidProduct");
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
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ValidProduct");
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

    // A server that is not there at all, rather than one that refuses the login: nothing listens on the port.
    [Test]
    public void TestConnection_UnreachableServer_FailsWithoutThrowing()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            config["SchemaPackagePath"] = TestHelper.GetTestProductPath(ProductPlatformFolder, "ValidProduct");
            var originalServer = config["Target:Server"];
            var originalPort = config["Target:Port"];
            config["Target:Server"] = "127.0.0.1";
            config["Target:Port"] = ClosedPort().ToString();
            var progressErrors = new List<string>();
            var progressLog = Substitute.For<log4net.ILog>();
            progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => progressErrors.Add(ci.Arg<object>()?.ToString() ?? ""));
            LogFactory.Register("ProgressLog", progressLog);
            LogFactory.Register("FailureLog", Substitute.For<log4net.ILog>());
            try
            {
                var pq = new ProductQuench();
                Assert.That(pq.RunPreFlight(previewTargets: false), Is.False);
                Assert.That(pq.Failed, Is.True);
                Assert.That(progressErrors, Has.Some.Contains("**CONNECTION FAILED**"),
                    "the server check reports the failed connection: " + string.Join(" | ", progressErrors));
            }
            finally
            {
                LogFactory.Clear();
                config["Target:Server"] = originalServer;
                config["Target:Port"] = originalPort;
                config["SchemaPackagePath"] = null;
            }
        }
    }

    // A port the OS just handed out and took back, so a connection to it is refused at once rather than timing out.
    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
