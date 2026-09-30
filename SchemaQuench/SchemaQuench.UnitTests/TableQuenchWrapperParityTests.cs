// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Every step an engine's <c>TableQuench</c> SQL wrapper runs must also be run by SchemaQuench's own deploy path.
/// <para>The integration suite exercises tables almost entirely through that wrapper, and the product never calls it —
/// <c>DatabaseQuench</c> runs each step itself. So a step added only to the wrapper passes every test while no real
/// deploy runs it. Four SQL Server steps went out that way: CDC (#423), table Change Tracking and FILESTREAM columns
/// (#424), and the below-floor degrade (#425). This fails the build the moment the two lists disagree.</para>
/// </summary>
[TestFixture]
public class TableQuenchWrapperParityTests
{
    [TestCase("SqlServer", "SchemaSmith.TableQuench.sql")]
    [TestCase("PostgreSQL", "SchemaSmith.TableQuench.sql")]
    [TestCase("MySQL", "SchemaSmith_TableQuench.sql")]
    public void EveryStepTheWrapperRuns_IsRunByTheDeployPath(string engine, string wrapperFile)
    {
        var root = RepoRoot();
        Assert.That(root, Is.Not.Null, "could not find SchemaSmith.sln above the test directory");

        var wrapper = File.ReadAllText(Path.Join(root, "Schema", "Scripts", engine, wrapperFile));
        var steps = Regex.Matches(wrapper, @"(?:EXEC|CALL)\s+""?SchemaSmith""?[._]""?(\w+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.That(steps, Is.Not.Empty, $"no steps parsed from {engine}/{wrapperFile} -- the pattern no longer matches it");

        var deployPath = string.Join("\n", Directory.GetFiles(Path.Join(root, "SchemaQuench"), "*.cs")
            .Select(File.ReadAllText));
        // The C# source quotes PostgreSQL identifiers inside verbatim strings, so a call reads ""SchemaSmith"".""X"".
        var missing = steps.Where(step => !Regex.IsMatch(deployPath, $@"SchemaSmith""*[._]""*{Regex.Escape(step)}\b")).ToList();

        Assert.That(missing, Is.Empty,
            $"{engine}: the TableQuench wrapper runs {string.Join(", ", missing)}, but SchemaQuench's deploy path never " +
            "does, so a real deploy skips it while every wrapper-based test passes. Call it from DatabaseQuench.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
