// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;

namespace Schema.IntegrationTests;

public static class TestHelper
{
#nullable enable
    private static string? _solutionRoot;
#nullable restore

    public static string SolutionRoot => _solutionRoot ??= FindSolutionRoot();

    public static string TestProductsPath => Path.Combine(SolutionRoot, "TestProducts");

    public static string GetTestProductPath(string platform, string productName) =>
        Path.Combine(TestProductsPath, platform, productName);

    // A test that adds or removes a file in a product edits a copy: the SQL Server tests run as several processes, and
    // another process deploying the shared folder would read the half-made change.
    public static string CopyTestProduct(string platform, string productName)
    {
        var source = GetTestProductPath(platform, productName);
        var copy = Path.Join(Path.GetTempPath(), $"{productName}_{Guid.NewGuid():N}");
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Join(copy, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Join(copy, Path.GetRelativePath(source, file)));
        return copy;
    }

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "SchemaSmith.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException(
            "Could not find solution root (looked for SchemaSmith.sln walking up from " + AppContext.BaseDirectory + ")");
    }
}
