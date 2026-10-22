// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Schema.Domain.MySQL;

namespace Schema.Utility;

/// <summary>
/// A FULLTEXT index's parser (<c>WITH PARSER ngram</c>) is reported by neither engine's INFORMATION_SCHEMA, only by
/// <c>SHOW CREATE TABLE</c>, which a stored procedure cannot read. Extraction reads it from that text instead.
/// </summary>
public static class MySqlFullTextParsers
{
    // MySQL writes the clause inside a version comment (/*!50100 WITH PARSER `ngram` */); a bare form is accepted too.
    private static readonly Regex FullTextLine = new(
        @"^\s*FULLTEXT\s+(?:KEY|INDEX)\s+`(?<name>(?:[^`]|``)+)`[^\r\n]*?WITH\s+PARSER\s+(?:`(?<parser>(?:[^`]|``)+)`|(?<parser>[^`\s*,)]+))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static Dictionary<string, string> Read(string createTable)
    {
        var parsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(createTable)) return parsers;
        foreach (Match m in FullTextLine.Matches(createTable))
            parsers[m.Groups["name"].Value.Replace("``", "`")] = m.Groups["parser"].Value.Replace("``", "`");
        return parsers;
    }

    /// <summary>Sets each FULLTEXT index's <see cref="FullTextIndex.Parser"/> from the table's <c>SHOW CREATE TABLE</c> text.</summary>
    public static void Apply(MySqlTable table, string createTable)
    {
        var parsers = Read(createTable);
        if (parsers.Count == 0) return;
        foreach (var index in table.FullTextIndexes)
        {
            var name = index.Name?.Trim();
            if (name is { Length: > 1 } && name[0] == '`' && name[^1] == '`')
                name = name[1..^1].Replace("``", "`");
            if (name != null && string.IsNullOrEmpty(index.Parser) && parsers.TryGetValue(name, out var parser))
                index.Parser = parser;
        }
    }
}
