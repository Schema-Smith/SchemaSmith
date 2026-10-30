// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;

namespace SchemaTongs.IntegrationTests.Shared;

internal static class ExtractedJson
{
    // An extracted package writes a key only when it holds a value, on every engine, so a package's shape does not
    // depend on the engine it came from.
    public static void HasNoEmptyValues(IFile file)
    {
        var empties = file.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IFile.WriteAllText))
            .Select(c => c.GetArguments())
            .Where(a => a[0] is string path && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => Regex.Matches((string)a[1]!, "\"(\\w+)\": \"\"")
                .Select(m => $"{Path.GetFileName((string)a[0]!)}: {m.Groups[1].Value}"))
            .ToList();
        Assert.That(empties, Is.Empty, "a key written with an empty value");
    }
}
