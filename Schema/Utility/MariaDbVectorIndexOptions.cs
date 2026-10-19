// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Domain.MariaDb;

namespace Schema.Utility;

/// <summary>
/// A MariaDB VECTOR index's <c>M</c> and <c>DISTANCE</c> are in no INFORMATION_SCHEMA table before 13.1, only in
/// <c>SHOW CREATE TABLE</c>, which writes each one only when it was declared. Extraction reads them from that text.
/// </summary>
public static class MariaDbVectorIndexOptions
{
    private static readonly Regex VectorLine = new(
        @"^\s*VECTOR\s+(?:KEY|INDEX)\s+`(?<name>(?:[^`]|``)+)`(?<rest>[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static readonly Regex Option = new(
        @"`?(?<key>M|DISTANCE)`?\s*=\s*'?(?<value>[A-Za-z0-9_]+)'?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static Dictionary<string, (int? M, string Distance)> Read(string createTable)
    {
        var options = new Dictionary<string, (int? M, string Distance)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(createTable)) return options;
        foreach (Match line in VectorLine.Matches(createTable))
        {
            // The options follow the column list; a COMMENT before them is quoted and cannot hold the closing paren.
            var rest = line.Groups["rest"].Value;
            var afterColumns = rest[(rest.IndexOf(')') + 1)..];
            afterColumns = Regex.Replace(afterColumns, @"COMMENT\s+'(?:[^']|'')*'", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            int? m = null;
            string distance = null;
            foreach (Match option in Option.Matches(afterColumns))
            {
                if (option.Groups["key"].Value.Equals("M", StringComparison.OrdinalIgnoreCase) && int.TryParse(option.Groups["value"].Value, out var value))
                    m = value;
                else if (option.Groups["key"].Value.Equals("DISTANCE", StringComparison.OrdinalIgnoreCase))
                    distance = option.Groups["value"].Value.ToLowerInvariant();
            }
            options[line.Groups["name"].Value.Replace("``", "`")] = (m, distance);
        }
        return options;
    }

    /// <summary>Sets each VECTOR index's <see cref="MariaDbIndex.M"/> and <see cref="MariaDbIndex.Distance"/>.</summary>
    public static void Apply(IEnumerable<Schema.Domain.Index> indexes, string createTable)
    {
        var options = Read(createTable);
        if (options.Count == 0) return;
        foreach (var index in indexes.OfType<MariaDbIndex>())
        {
            var name = index.Name?.Trim();
            if (name is { Length: > 1 } && name[0] == '`' && name[^1] == '`')
                name = name[1..^1].Replace("``", "`");
            if (name == null || !options.TryGetValue(name, out var found)) continue;
            index.M ??= found.M;
            index.Distance ??= found.Distance;
        }
    }
}
