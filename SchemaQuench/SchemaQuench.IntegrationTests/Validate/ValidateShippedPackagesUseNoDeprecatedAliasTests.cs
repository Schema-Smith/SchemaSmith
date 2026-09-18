// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.Validate;

/// <summary>
/// No package this repository ships or tests with may depend on a deprecated alias. A package that does
/// still deploys -- load migrates the alias -- which is exactly why it goes unnoticed: until SS-DEP-* existed,
/// --Validate called the MySQL and MariaDB demos a clean PASS while every one of them used
/// <c>SchemaIdentificationScript</c>. Retiring an alias then breaks every consumer that deploys these
/// packages, from a push that changed nothing in them. This fails here first instead.
/// <para>Runs the real validator over the real trees rather than grepping for key names, so an alias added
/// later is covered the moment its migration records a notice.</para>
/// </summary>
[TestFixture]
[Category("Validate")]
public class ValidateShippedPackagesUseNoDeprecatedAliasTests : ValidateFixtureTestBase
{
    [TestCase("Demos")]
    [TestCase("TestProducts")]
    public void NoPackage_ReportsADeprecation(string tree)
    {
        var packages = PackagesUnder(Path.Join(RepoRoot(), tree));
        Assert.That(packages, Is.Not.Empty, $"found no packages under {tree} -- the scan proves nothing");

        var offenders = new List<string>();
        foreach (var package in packages)
            offenders.AddRange(RunValidate(package).Findings
                .Where(f => f.Code.StartsWith("SS-DEP-"))
                .Select(f => $"{Path.GetRelativePath(RepoRoot(), package)}: {f.Code} {f.Location}"));

        Assert.That(offenders, Is.Empty,
            $"{offenders.Count} deprecated-alias use(s) in {tree} ({packages.Count} packages scanned):\n  " + string.Join("\n  ", offenders));
    }

    // A package is a directory holding both Product.json and Templates/ -- a table merely named Product.json
    // is not one.
    private static List<string> PackagesUnder(string root) =>
        Directory.GetFiles(root, "Product.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(d => Directory.Exists(Path.Join(d, "Templates")))
            .OrderBy(d => d)
            .ToList();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(ValidateShippedPackagesUseNoDeprecatedAliasTests).Assembly.Location)!);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("SchemaSmith.sln not found above the test assembly");
    }
}
