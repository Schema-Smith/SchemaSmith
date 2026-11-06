// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
//
// SqlBodyRewriter — schema-template extraction support (design §7.3, §7.4).
//
// IMPLEMENTATION NOTE: This rewriter is intentionally regex-based with single-quoted
// string-literal masking. A full T-SQL / PL/pgSQL lexer would be more correct (e.g.
// for double-quoted PostgreSQL string literals, dollar-quoted blocks, nested block
// comments), but the value-to-effort tradeoff at slice 6 didn't justify a dedicated
// parser — the rewriter runs ONCE at extraction time and the output is reviewed by
// a human (via the §7.4 unqualified-identifier audit log) before quench. Known
// limitations explicitly documented + tested:
//
//   * Standard <c>'...'</c> single-quoted string literals ARE masked, so a source-
//     schema name embedded in such a literal is preserved verbatim. SQL Server's
//     <c>N'...'</c> Unicode literal is recognised the same way.
//   * PostgreSQL dollar-quoted blocks (<c>$tag$ ... $tag$</c>) are NOT recognised;
//     content inside them is treated as ordinary SQL. In practice this matters for
//     PL/pgSQL function bodies extracted from <c>pg_proc.prosrc</c>. The conservative
//     audit warning surfaces the affected files for human review (§7.4).
//   * The SCHEMA half of a `schema.object` reference is matched whether bracket-
//     delimited, double-quoted, or bare, independently of how the OBJECT half is
//     quoted (issue #272 mixed forms). The object token is preserved verbatim.
//
// The rewriter is invoked by SchemaTongs when in schema-template mode (i.e. when
// Source.Schema is non-empty in SchemaTongs.settings.json). The
// SqlBodyRewriterResult struct carries the rewritten body plus a list of 1-based
// line numbers where unqualified table-like references were detected; the caller
// aggregates these into the per-file warning emission.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Domain;

namespace SchemaTongs;

/// <summary>
/// Result of rewriting one SQL body against a source schema (design §7.3 / §7.4).
/// Both fields are non-null after a successful call — <see cref="UnqualifiedReferenceLines"/>
/// is an empty list when the body contained no detectable unqualified references.
/// </summary>
public class SqlBodyRewriterResult
{
    /// <summary>The rewritten body — source-schema-qualified refs collapsed to <c>{{SchemaName}}.&lt;name&gt;</c>.</summary>
    public string Body { get; init; }

    /// <summary>
    /// 1-based line numbers in the original body where an unqualified identifier
    /// was observed in a table-position context (after FROM, JOIN, INTO, UPDATE).
    /// Empty list when no such references were found.
    /// </summary>
    public IReadOnlyList<int> UnqualifiedReferenceLines { get; init; }
}

/// <summary>
/// Rewrites source-schema-qualified identifiers in extracted SQL bodies to the
/// schema-template engine token <c>{{SchemaName}}</c> and audits unqualified
/// identifier references. See file header for design rationale and limitations.
/// </summary>
public static class SqlBodyRewriter
{
    private const string SchemaToken = SchemaDefaultResolver.SchemaNameToken; // "{{SchemaName}}"

    // String-literal mask placeholders. A literal index N is encoded as `N`
    // to make the boundary obvious and unambiguous even if the user includes digits in
    // their SQL — control characters are not legal SQL syntax outside string literals.
    private const char MaskStart = '';
    private const char MaskEnd = '';

    // Regex pattern matching a single-quoted SQL string literal, including doubled-up
    // single-quote escape sequences (`''`). The optional `N` prefix handles SQL Server's
    // Unicode literals. This is the standard pattern used in T-SQL lexers when full
    // lexing is overkill.
    // Comments are masked too: an apostrophe in one used to open a fake literal running to the next quote, hiding the
    // references in between. Bracketed and double-quoted identifiers are matched only to be kept as they are.
    private static readonly Regex StringLiteralPattern = new(
        @"(?<keep>\[(?:[^\]]|\]\])*\]|""(?:[^""]|"""")*"")|--[^\r\n]*|/\*.*?\*/|N?'(?:[^']|'')*'",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static SqlBodyRewriterResult Rewrite(string body, string sourceSchema)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(sourceSchema))
        {
            return new SqlBodyRewriterResult
            {
                Body = body ?? "",
                UnqualifiedReferenceLines = Array.Empty<int>()
            };
        }

        // Step 1: mask single-quoted string literals so their content is invisible to the
        // subsequent regex passes. We restore them at the end.
        var literals = new List<string>();
        var masked = StringLiteralPattern.Replace(body, m =>
        {
            if (m.Groups["keep"].Success) return m.Value;
            literals.Add(m.Value);
            return $"{MaskStart}{literals.Count - 1}{MaskEnd}";
        });

        // Step 2: rewrite source-schema-qualified references. The schema half may be
        // bracket-delimited, double-quoted, or bare — independently of how the object half
        // is quoted — so one pattern enumerates the three schema wrappings and a lookahead
        // leaves the object token untouched. Issue #272: mixed forms such as `[src].name`
        // (emitted by the SSMS view designer) were previously missed by the same-wrapper
        // patterns. The leading lookbehind blocks substring (`tenant_seed_audit`) and
        // alias.column (`c.tenant_seed`) false matches; the trailing lookahead requires a
        // following object token (word / quote / bracket).
        var escaped = Regex.Escape(sourceSchema);
        var sourceQualified = new Regex(
            $@"(?<![\w.])(?:\[{escaped}\]|""{escaped}""|\b{escaped}\b)\s*\.\s*(?=[\w""\[])",
            RegexOptions.Compiled);
        masked = sourceQualified.Replace(masked, $"{SchemaToken}.");

        // Step 3: walk the masked body line-by-line for unqualified table-like references.
        // The audit is best-effort — see §7.4: we never auto-prefix, we just surface lines
        // for a human review.
        var unqualifiedLines = DetectUnqualifiedReferenceLines(masked);

        // Step 4: restore the masked string literals.
        var restored = RestoreLiterals(masked, literals);

        return new SqlBodyRewriterResult
        {
            Body = restored,
            UnqualifiedReferenceLines = unqualifiedLines
        };
    }

    private static string RestoreLiterals(string masked, List<string> literals)
    {
        if (literals.Count == 0) return masked;

        var placeholder = new Regex($"{MaskStart}(\\d+){MaskEnd}", RegexOptions.Compiled);
        return placeholder.Replace(masked, m =>
        {
            var idx = int.Parse(m.Groups[1].Value);
            return idx < literals.Count ? literals[idx] : m.Value;
        });
    }

    // Detects identifier references in table-position contexts on each line and returns
    // the (1-based) line numbers where an unqualified identifier appears in a FROM, JOIN,
    // INTO, or UPDATE clause. Identifier here means a single word character sequence
    // (or bracketed/double-quoted identifier) that is NOT already qualified by a `.`.
    private static readonly Regex TablePositionRefPattern = new(
        @"\b(?<kw>FROM|JOIN|INTO|UPDATE)\b\s+(?<ref>(\[[^\]]+\]|""[^""]+""|\w+)(?:\s*\.\s*(\[[^\]]+\]|""[^""]+""|\w+))?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<int> DetectUnqualifiedReferenceLines(string body)
    {
        var lines = new List<int>();
        if (string.IsNullOrEmpty(body)) return lines;

        // Normalize newlines to keep the line-number arithmetic predictable.
        var split = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < split.Length; i++)
        {
            var line = split[i];

            foreach (Match m in TablePositionRefPattern.Matches(line))
            {
                var refText = m.Groups["ref"].Value;
                // Qualified by any schema (`dbo.Countries`, `[dbo].[Countries]`, `{{SchemaName}}.Customers`, etc.).
                // After step-2 rewrites, source-schema-qualified refs are already `{{SchemaName}}.<name>`,
                // so this single dot-check covers both literal-schema and template-token cases.
                if (refText.Contains('.')) continue;
                // Common SQL keywords that look like identifiers after FROM (e.g. FROM (SELECT)).
                if (IsLikelyKeywordOrLiteral(refText)) continue;

                lines.Add(i + 1); // 1-based for human-readability in the warning output.
                break;            // one report per line is enough.
            }
        }

        return lines;
    }

    private static readonly HashSet<string> KeywordSentinels = new(StringComparer.OrdinalIgnoreCase)
    {
        "(", "SELECT", "VALUES", "OPENJSON", "OPENROWSET", "OPENQUERY", "OPENXML",
        "INSERTED", "DELETED", "DUAL"
    };

    private static bool IsLikelyKeywordOrLiteral(string refText)
        => KeywordSentinels.Contains(refText) || refText.StartsWith("(", StringComparison.Ordinal);
}
