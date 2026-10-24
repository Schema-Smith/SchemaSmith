// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Validate;

/// <summary>
/// The exit code --Validate ends with, driven through <see cref="Program.Main"/>: a package with warnings and no errors
/// exits 1 under --ExitNonZeroOnWarning and 0 without it. Only the helper that applies the switch was tested, so the
/// count of warnings Main hands it could have been dropped with nothing turning red.
/// </summary>
[TestFixture]
[Category("Validate")]
public class ValidateExitCodeTests : ValidateFixtureTestBase
{
    private (IEnvironment Environment, ILog Progress) RunMain(string commandLine, string scrubArtifacts = null)
    {
        var settings = new List<KeyValuePair<string, string>> { new("SchemaPackagePath", FixturePath("DanglingFkPartialDeployment", "SqlServer")) };
        if (scrubArtifacts != null) settings.Add(new("ScrubArtifacts", scrubArtifacts));
        FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var environment = Substitute.For<IEnvironment>();
        environment.CommandLine.Returns(commandLine);
        FactoryContainer.Register(environment);
        var progress = Substitute.For<ILog>();
        LogFactory.Register("ProgressLog", progress);
        LogFactory.Register("ErrorLog", Substitute.For<ILog>());
        try
        {
            Program.Main([]);
        }
        finally
        {
            LogFactory.Clear();
            FactoryContainer.Unregister<IEnvironment>();
        }
        return (environment, progress);
    }

    [Test]
    public void APackageWithWarningsOnly_ExitsOne_UnderExitNonZeroOnWarning_AndZeroWithout()
    {
        var withSwitch = RunMain("--Validate --ExitNonZeroOnWarning").Environment;
        var without = RunMain("--Validate").Environment;

        Assert.Multiple(() =>
        {
            withSwitch.Received(1).Exit(1);
            without.Received(1).Exit(0);
        });
    }

    // #576: the setting does nothing now, so a run that sets it says so.
    [Test]
    public void ARunThatSetsScrubArtifacts_IsWarnedThatItIsDeprecated()
    {
        var progress = RunMain("--Validate", scrubArtifacts: "true").Progress;

        progress.Received().Warn(Arg.Is<object>(m => m.ToString().Contains("ScrubArtifacts is deprecated")));
    }
}
