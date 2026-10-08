// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Utility;
using System.Collections.Generic;
using System.IO;
using Schema.Isolators;

namespace SchemaShears;

public class PatchBuildException : RunFailedException
{
    public PatchBuildException(string message) : base(message) { }
}

public static class PatchManifest
{
    public static IReadOnlyList<string> Read(string manifestPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !FileWrapper.GetFromFactory().Exists(manifestPath))
            throw new PatchBuildException($"Manifest file not found: '{manifestPath}'.");

        var entries = new List<string>();
        foreach (var raw in FileWrapper.GetFromFactory().ReadAllLines(manifestPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            var normalized = line.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (!FileWrapper.GetFromFactory().Exists(Path.Join(sourcePath, normalized)))
                throw new PatchBuildException($"Manifest path does not exist under source: '{line}'.");

            entries.Add(normalized);
        }

        if (entries.Count == 0)
            throw new PatchBuildException($"Manifest '{manifestPath}' contains no usable entries.");

        return entries;
    }
}
