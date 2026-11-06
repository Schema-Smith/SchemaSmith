// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using System.Text.RegularExpressions;

namespace SchemaTongs;

/// <summary>
/// A module's text with its comments and string literals blanked to spaces, character for character, so a pattern run over
/// it finds only code and every position it reports is a position in the original. Bracketed and double-quoted identifiers
/// are kept: a quote or a double dash inside one is part of a name.
/// </summary>
internal static class SqlTextMask
{
    private static readonly Regex Tokens = new(
        @"(?<keep>\[(?:[^\]]|\]\])*\]|""(?:[^""]|"""")*"")|(?<blank>--[^\r\n]*|/\*[\s\S]*?\*/|N?'(?:[^']|'')*')",
        RegexOptions.Compiled);

    internal static string Blank(string sql) =>
        string.IsNullOrEmpty(sql)
            ? sql
            : Tokens.Replace(sql, m => m.Groups["blank"].Success
                ? new string(m.Value.Select(c => c is '\r' or '\n' ? c : ' ').ToArray())
                : m.Value);
}
