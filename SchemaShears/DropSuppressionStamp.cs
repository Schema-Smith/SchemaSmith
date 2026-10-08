// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaShears;

public static class DropSuppressionStamp
{
    // Indexes covers both ways an index leaves a table: one the product no longer declares (owned, and dropped by
    // default) and one the package never declared (out-of-band).
    private static readonly Dictionary<string, string[]> CategoryToFlags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tables"]             = ["DropTablesRemovedFromProduct"],
            ["Columns"]            = ["DropColumnsRemovedFromProduct"],
            ["Indexes"]            = ["DropUnknownIndexes", "DropIndexesRemovedFromProduct"],
            ["ForeignKeys"]        = ["DropForeignKeysRemovedFromProduct"],
            ["CheckConstraints"]   = ["DropCheckConstraintsRemovedFromProduct"],
            ["ExcludeConstraints"] = ["DropExcludeConstraintsRemovedFromProduct"],
            ["Statistics"]         = ["DropStatisticsRemovedFromProduct"],
        };

    public static void Apply(string productJsonPath, IReadOnlyCollection<string> allowDrops)
    {
        if (!FileWrapper.GetFromFactory().Exists(productJsonPath))
            throw new PatchBuildException($"Product.json not found in patch output: '{productJsonPath}'.");

        ValidateCategories(allowDrops);

        var json = JsonText.ParseObject(FileWrapper.GetFromFactory().ReadAllText(productJsonPath));
        var platform = Enum.TryParse<Platform>(json["Platform"]?.Value<string>(), ignoreCase: true, out var p) ? p : Platform.Unknown;

        foreach (var (category, flags) in CategoryToFlags)
        {
            if (allowDrops.Contains(category, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var flag in flags.Where(f => AppliesTo(f, platform)))
                json[flag] = false;
        }

        FileWrapper.GetFromFactory().WriteAllText(productJsonPath, json.ToString(Formatting.Indented));
    }

    // Checked before anything is written as well: a typo found after the copy left a half-built patch behind, and the
    // re-run then failed because the output already existed.
    public static void ValidateCategories(IReadOnlyCollection<string> allowDrops)
    {
        var unknown = allowDrops
            .Where(c => !CategoryToFlags.ContainsKey(c))
            .ToList();

        if (unknown.Count > 0)
        {
            var valid = string.Join(", ", CategoryToFlags.Keys);
            throw new PatchBuildException(
                $"Unknown drop category '{unknown[0]}'. Valid categories: {valid}.");
        }
    }

    // A setting scoped to some engines is not part of the product schema on the others, so stamping it there
    // made every patch of a SQL Server, MySQL or MariaDB product fail --Validate with SS-JSON-001 -- and it never
    // suppressed anything, because those engines have no such objects. The scope is read from the same
    // [SchemaProperty(Platforms)] the schema generator reads, so the two cannot drift. An unrecognised platform
    // stamps everything, as before.
    private static bool AppliesTo(string flag, Platform platform)
    {
        if (platform == Platform.Unknown)
            return true;
        var scoped = typeof(Product).GetProperty(flag)?.GetCustomAttribute<SchemaPropertyAttribute>()?.Platforms;
        return scoped == null || scoped.Length == 0 || scoped.Contains(platform);
    }
}
