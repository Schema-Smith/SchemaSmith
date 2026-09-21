// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Domain;

namespace Schema.Utility;

/// <summary>
/// The JSON-path-to-column mapping for the working set, READ OUT OF THE SHRED ITSELF rather than
/// transcribed alongside it.
/// <para>
/// The parse script already declares the mapping, once, in each <c>OPENJSON(…) WITH (…)</c> block:
/// <c>[FillFactor] TINYINT '$.FillFactor'</c> says where that column's value comes from. A client-side
/// ingest path needs exactly the same mapping, and writing it out a second time in C# would be a
/// hand-maintained copy of 177 columns whose only job is to stay identical to the SQL -- the drift risk
/// the whole two-producer design turns on. So the map is parsed from the script at load and both paths
/// are driven by one declaration: edit the WITH block and the client follows.
/// </para>
/// <para>
/// This deliberately does NOT try to be a SQL parser. It reads one machine-written construct with a
/// fixed shape, and every table it is asked for must be found or it throws -- a mapping that silently
/// came back short would produce a working set missing columns, which is the failure mode this class
/// exists to make impossible.
/// </para>
/// </summary>
public static class WorkingSetShredMap
{
    /// <summary>One column of a shred: the working-set column and the JSON it is read from.</summary>
    /// <param name="Column">Working-set column name, unbracketed.</param>
    /// <param name="SqlType">The type the shred declares, e.g. <c>NVARCHAR(500)</c>.</param>
    /// <param name="JsonPath">The declared path, e.g. <c>$.FillFactor</c>.</param>
    /// <param name="AsJson">
    /// True for <c>AS JSON</c> columns, which carry a nested array or object as raw text rather than a
    /// scalar. These are what let a partially-migrated path work: a row can be bulk-loaded with its
    /// children still in JSON, and the child shred below reads them exactly as it always did.
    /// </param>
    public sealed record ShredColumn(string Column, string SqlType, string JsonPath, bool AsJson);

    // The whole construct, anchored on the INSERT so the right OPENJSON is found. #TableDefinitions is
    // preceded by a guard that shreds the same payload with a two-column WITH to report a missing
    // Schema; anchoring on the INSERT is what tells the real mapping from that guard.
    //
    // The gap between the INSERT and its WITH block is tempered so it cannot run past the NEXT INSERT.
    // Left as a plain lazy wildcard it did exactly that: #Tables is derived and has no shred of its own,
    // so the match ran on and claimed the #Columns block below it -- mapping a table the client cannot
    // produce, and losing the one it can. Both faults were silent.
    private static readonly Regex InsertBlock = new(
        @"INSERT\s+INTO\s+(?<table>#\w+)\b(?:(?!INSERT\s+INTO\s+#).)*?OPENJSON\s*\((?<source>[^)]*?)\)\s*WITH\s*\((?<body>.*?)\n\s*\)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ColumnDecl = new(
        @"\[(?<col>[^\]]+)\]\s+(?<type>[A-Za-z]+(?:\s*\([^)]*\))?)\s*'(?<path>[^']*)'(?<json>\s+AS\s+JSON)?",
        RegexOptions.Compiled);

    private static readonly Regex LineComment = new(@"--[^\n]*", RegexOptions.Compiled);

    /// <summary>
    /// Parse every shred block in the given ParseTableJson script, keyed by working-set table name.
    /// Tables that are DERIVED rather than shredded (<c>#Tables</c> is built from #TableDefinitions)
    /// have no block and are simply absent -- a caller asking for one is asking for something the
    /// client cannot produce, and <see cref="For"/> says so.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ShredColumn>> Parse(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("The parse script is empty, so no shred mapping can be read from it.", nameof(script));

        // Commented-out shapes must never register: a column left behind in a comment would be mapped
        // and then bulk-loaded into a table that no longer has it.
        var clean = LineComment.Replace(script, "");

        var map = new Dictionary<string, IReadOnlyList<ShredColumn>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in InsertBlock.Matches(clean))
        {
            var table = block.Groups["table"].Value;
            if (map.ContainsKey(table)) continue; // first (and only) shred per table wins

            var columns = ColumnDecl.Matches(block.Groups["body"].Value)
                .Select(m => new ShredColumn(
                    m.Groups["col"].Value,
                    Regex.Replace(m.Groups["type"].Value, @"\s+", ""),
                    m.Groups["path"].Value,
                    m.Groups["json"].Success))
                .ToList();

            if (columns.Count > 0) map[table] = columns;
        }

        return map;
    }

    /// <summary>The shred mapping for one working-set table, for the platform's parse script.</summary>
    public static IReadOnlyList<ShredColumn> For(Platform platform, string table)
    {
        var map = Parse(ForgeKindler.GetParseTableJsonScript(platform));
        if (!map.TryGetValue(table, out var columns))
            throw new Exception(
                $"No OPENJSON shred block for '{table}' in the {platform} parse script. Either the table is " +
                "derived rather than shredded, or the block's shape changed and the mapping can no longer " +
                "be read from it.");
        return columns;
    }
}
