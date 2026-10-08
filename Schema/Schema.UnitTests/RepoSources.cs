// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Schema.UnitTests;

/// <summary>
/// The repository's C# sources, for the tests that scan them. Build output is skipped while walking rather than
/// filtered afterwards: listing every file under bin/ and obj/ (test fixtures are copied there per project) made
/// those tests the slowest in the suite, 25-40 seconds each. Each list is walked once per test run.
/// </summary>
internal static class RepoSources
{
    private static readonly string[] Projects = ["Schema", "SchemaQuench", "SchemaTongs", "DataTongs", "SchemaShears"];

    private static readonly Lazy<string> RootPath = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static readonly Lazy<IReadOnlyList<string>> WithTests = new(() => Walk(includeTestProjects: true));
    private static readonly Lazy<IReadOnlyList<string>> WithoutTests = new(() => Walk(includeTestProjects: false));

    public static string Root => RootPath.Value;

    /// <summary>Every .cs file in the product projects; test projects included only when asked.</summary>
    public static IReadOnlyList<string> CSharpFiles(bool includeTestProjects) =>
        includeTestProjects ? WithTests.Value : WithoutTests.Value;

    private static IReadOnlyList<string> Walk(bool includeTestProjects)
    {
        if (Root == null) return [];
        return Projects
            .Select(project => Path.Join(Root, project))
            .Where(Directory.Exists)
            .SelectMany(dir => CSharpFilesBelow(dir, includeTestProjects))
            .ToList();
    }

    private static IEnumerable<string> CSharpFilesBelow(string dir, bool includeTestProjects)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            yield return file;

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj") continue;
            if (!includeTestProjects && name.Contains("Tests", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in CSharpFilesBelow(sub, includeTestProjects))
                yield return file;
        }
    }
}
