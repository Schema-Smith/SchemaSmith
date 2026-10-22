// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Text;

namespace Schema.DataAccess;

/// <summary>
/// Splits PostgreSQL scripts into individual statements. A semicolon ends a statement only outside a dollar-quoted block
/// (<c>$$</c> or a named tag such as <c>$function$</c>), a string literal (<c>'...'</c>, including <c>E'...'</c> with
/// backslash escapes), a quoted identifier (<c>"..."</c>), a <c>--</c> or <c>/* */</c> comment (block comments nest), and
/// a SQL-standard routine body (<c>BEGIN ATOMIC ... END</c>, whose own <c>CASE ... END</c> pairs are counted). Comment text
/// is preserved verbatim in the output statement.
/// </summary>
public static class PostgreSqlStatementSplitter
{
    public static List<string> Split(string script)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(script)) return results;

        var current = new StringBuilder();
        // Inside BEGIN ATOMIC: 1 for the body itself, plus one per open CASE.
        var atomicDepth = 0;
        var i = 0;

        while (i < script.Length)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == '$' && ReadDollarTag(script, i) is { } tag)
            {
                var close = script.IndexOf(tag, i + tag.Length, System.StringComparison.Ordinal);
                var end = close < 0 ? script.Length : close + tag.Length;
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '-' && next == '-')
            {
                var eol = script.IndexOf('\n', i);
                var end = eol < 0 ? script.Length : eol;
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = SkipBlockComment(script, i);
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '\'')
            {
                var escapes = i > 0 && (script[i - 1] == 'E' || script[i - 1] == 'e') && (i < 2 || !IsWordChar(script[i - 2]));
                var end = SkipQuoted(script, i, '\'', escapes);
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '"')
            {
                var end = SkipQuoted(script, i, '"', backslashEscapes: false);
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (IsWordStart(script, i))
            {
                var wordEnd = i;
                while (wordEnd < script.Length && IsWordChar(script[wordEnd])) wordEnd++;
                var word = script.Substring(i, wordEnd - i);
                if (atomicDepth == 0)
                {
                    if (word.Equals("BEGIN", System.StringComparison.OrdinalIgnoreCase) && NextWordIs(script, wordEnd, "ATOMIC"))
                        atomicDepth = 1;
                }
                else if (word.Equals("CASE", System.StringComparison.OrdinalIgnoreCase))
                    atomicDepth++;
                else if (word.Equals("END", System.StringComparison.OrdinalIgnoreCase))
                    atomicDepth--;
                current.Append(word);
                i = wordEnd;
                continue;
            }

            if (c == ';' && atomicDepth == 0)
            {
                current.Append(';');
                var statement = current.ToString().Trim();
                if (!string.IsNullOrEmpty(statement))
                    results.Add(statement);
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        // Remaining content (no trailing semicolon)
        var remainder = current.ToString().Trim();
        if (!string.IsNullOrEmpty(remainder))
            results.Add(remainder);

        return results;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsWordStart(string s, int i) => (char.IsLetter(s[i]) || s[i] == '_') && (i == 0 || !IsWordChar(s[i - 1]));

    private static bool NextWordIs(string s, int from, string word)
    {
        var j = from;
        while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
        return j + word.Length <= s.Length
               && string.Compare(s, j, word, 0, word.Length, System.StringComparison.OrdinalIgnoreCase) == 0
               && (j + word.Length == s.Length || !IsWordChar(s[j + word.Length]));
    }

    // From the opening quote to just past the closing one. A doubled quote is an escaped quote; with backslash escapes
    // (E'...') a backslash escapes the next character.
    private static int SkipQuoted(string s, int start, char quote, bool backslashEscapes)
    {
        var j = start + 1;
        while (j < s.Length)
        {
            if (backslashEscapes && s[j] == '\\') { j += 2; continue; }
            if (s[j] == quote)
            {
                if (j + 1 < s.Length && s[j + 1] == quote) { j += 2; continue; }
                return j + 1;
            }
            j++;
        }
        return s.Length;
    }

    // PostgreSQL block comments nest.
    private static int SkipBlockComment(string s, int start)
    {
        var depth = 0;
        var j = start;
        while (j < s.Length)
        {
            if (s[j] == '/' && j + 1 < s.Length && s[j + 1] == '*')
            {
                depth++;
                j += 2;
            }
            else if (s[j] == '*' && j + 1 < s.Length && s[j + 1] == '/')
            {
                depth--;
                j += 2;
                if (depth == 0) return j;
            }
            else
                j++;
        }
        return s.Length;
    }

    /// <summary>
    /// Reads a dollar-quote tag starting at position start in script.
    /// Returns the full tag (e.g., "$$", "$function$", "$body$") or null if not a valid tag.
    /// A valid tag starts with $, contains only letters, digits, or underscores (or nothing), and ends with $.
    /// </summary>
    private static string ReadDollarTag(string script, int start)
    {
        if (script[start] != '$') return null;
        // A positional parameter ($1) or a $ inside an identifier is not a tag.
        if (start > 0 && IsWordChar(script[start - 1])) return null;

        var j = start + 1;
        if (j < script.Length && char.IsDigit(script[j])) return null;
        while (j < script.Length && script[j] != '$')
        {
            var c = script[j];
            // Tag body may only contain letters, digits, or underscores
            if (!char.IsLetterOrDigit(c) && c != '_')
                return null;
            j++;
        }

        if (j >= script.Length) return null; // No closing $
        // j is now at the closing $
        return script.Substring(start, j - start + 1);
    }
}
