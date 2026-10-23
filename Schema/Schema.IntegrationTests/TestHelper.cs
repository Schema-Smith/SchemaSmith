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

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "SchemaSmith.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException(
            "Could not find solution root (looked for SchemaSmith.sln walking up from " + AppContext.BaseDirectory + ")");
    }
}
