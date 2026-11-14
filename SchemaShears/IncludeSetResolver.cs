// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using Schema.Isolators;

namespace SchemaShears;

public static class IncludeSetResolver
{
    public static IReadOnlyDictionary<string, IncludeReason> Resolve(
        IReadOnlyList<string> manifest, IReadOnlyList<string> alwaysInclude, string sourcePath)
    {
        var set = new Dictionary<string, IncludeReason>();

        // Lowest precedence first; AddOrKeepStronger never downgrades a reason.
        AddOrKeepStronger(set, "Product.json", IncludeReason.Scaffolding);
        foreach (var path in alwaysInclude)
        {
            AddOrKeepStronger(set, path, IncludeReason.AlwaysInclude);
            AddTemplateScaffolding(set, path, sourcePath);
        }
        foreach (var path in manifest)
        {
            AddOrKeepStronger(set, path, IncludeReason.Manifest);
            AddTemplateScaffolding(set, path, sourcePath);
        }

        return set;
    }

    private static void AddTemplateScaffolding(IDictionary<string, IncludeReason> set, string relPath, string sourcePath)
    {
        var parts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 2 || !parts[0].Equals("Templates", System.StringComparison.OrdinalIgnoreCase)) return;

        var templateJson = Path.Join("Templates", parts[1], "Template.json");
        if (FileWrapper.GetFromFactory().Exists(Path.Join(sourcePath, templateJson)))
            AddOrKeepStronger(set, templateJson, IncludeReason.Scaffolding);
    }

    private static void AddOrKeepStronger(IDictionary<string, IncludeReason> set, string path, IncludeReason reason)
    {
        // Manifest=0 strongest, Scaffolding=2 weakest → keep the numerically smaller (stronger) reason.
        if (!set.TryGetValue(path, out var existing) || reason < existing)
            set[path] = reason;
    }
}
