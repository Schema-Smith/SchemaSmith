// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Validation.Checks;

/// <summary>
/// Reports a SQL Server package whose scripts use <c>CREATE OR ALTER</c> while its <c>MinimumVersion</c> admits a build
/// before 2016 SP1 (<c>SS-VER-002</c>, Warning). Those builds reject the statement as a syntax error, so the deploy
/// fails on the first module. One finding per package: the fix is the declared floor or the extraction form, not a
/// script. A package with no declared floor makes no claim and is not reported.
/// </summary>
public sealed class ModuleSyntaxCheck : ISchemaCheck
{
    private const string Code = "SS-VER-002";
    private const string Category = "Script";
    // 2017 is the first major whose every build accepts CREATE OR ALTER; "2016" admits the RTM build, which does not.
    private const int FirstMajorAcceptingCreateOrAlter = 14;
    private static readonly Regex CreateOrAlter = new(@"\bCREATE\s+OR\s+ALTER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        if (ctx.Platform != Platform.SqlServer || string.IsNullOrWhiteSpace(ctx.Product.MinimumVersion)) return [];
        var floor = VersionHelper.ParseDeclaredVersion(ctx.Product.MinimumVersion, Platform.SqlServer);
        // An unresolvable floor is SS-VER-001's finding.
        if (floor == null || floor >= FirstMajorAcceptingCreateOrAlter) return [];

        var dir = ProductDirectoryWrapper.GetFromFactory();
        var file = ProductFileWrapper.GetFromFactory();
        if (!dir.Exists(ctx.PackagePath)) return [];

        var scripts = dir.GetFiles(ctx.PackagePath, "*.sql", SearchOption.AllDirectories)
            .Where(path => file.Exists(path) && CreateOrAlter.IsMatch(file.ReadAllText(path)))
            .Select(path => RelativeTo(ctx.PackagePath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (scripts.Count == 0) return [];

        var named = string.Join(", ", scripts.Take(3)) + (scripts.Count > 3 ? ", ..." : "");
        return
        [
            new Finding(Severity.Warning, Code, Category, $"Product '{ctx.Product.Name}'",
                $"MinimumVersion '{ctx.Product.MinimumVersion}' admits SQL Server builds before 2016 SP1 (13.0.4001), which reject " +
                $"CREATE OR ALTER as a syntax error, and {scripts.Count} script{(scripts.Count == 1 ? " uses" : "s use")} it ({named}). " +
                "Declare MinimumVersion 2017 or later, or extract the package with ShouldCast:ModuleSyntax set to Legacy, " +
                "which writes each module in a form every supported version accepts.")
        ];
    }

    private static string RelativeTo(string root, string path)
    {
        var relative = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
        return relative.Replace('\\', '/').TrimStart('/');
    }
}
