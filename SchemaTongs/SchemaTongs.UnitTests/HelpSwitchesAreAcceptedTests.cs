// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.UnitTests;

// Every argument not on the tool's list stops the run, so a switch its --help offers but the list leaves out would
// refuse a legitimate command line. Read the switches from the help text itself and pass each one bare.
[NonParallelizable]
public class HelpSwitchesAreAcceptedTests
{
    [Test]
    public void EveryBareSwitchTheHelpLists_IsAccepted()
    {
        var program = typeof(global::SchemaTongs.Program);
        var known = (string[])program.GetField("KnownArguments", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var toolSwitches = (Action)Delegate.CreateDelegate(typeof(Action),
            program.GetMethod("ToolSpecificSwitches", BindingFlags.NonPublic | BindingFlags.Static)!);

        var environment = Substitute.For<IEnvironment>();
        var original = Console.Out;
        var help = new StringWriter();
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(environment);
            Console.SetOut(help);
            try
            {
                typeof(CommandLineParser).GetMethod("ShowHelpAndExit", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { "SchemaTongs", toolSwitches });
            }
            finally
            {
                Console.SetOut(original);
            }

            var bare = Regex.Matches(help.ToString(), @"--([A-Za-z]+)(?![A-Za-z_:=<])").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.That(bare, Is.Not.Empty, "the help text must list switches");
            try
            {
                foreach (var flag in bare)
                {
                    environment.CommandLine.Returns($"SchemaTongs.exe --{flag}");
                    Assert.That(CommandLineParser.UnrecognizedArguments(known), Is.Empty, $"--{flag} is in the help but would be refused");
                }
            }
            finally
            {
                FactoryContainer.Clear();
            }
        }
    }
}
