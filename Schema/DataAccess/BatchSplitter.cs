// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Schema.DataAccess;

public static class BatchSplitter
{
    /// <summary>
    /// Split a script that uses DELIMITER commands.
    /// MySQL client handles DELIMITER but ADO.NET does not, so we need to parse it.
    /// </summary>
    public static List<string> Split(string script)
    {
        var batches = new List<string>();
        if (script.Split('\n').Any(IsDelimiterCommand))
            SplitDelimitedScript(batches, script);
        else
            batches.Add(script);

        return batches;
    }

    private static bool IsDelimiterCommand(string line) =>
        line.Trim().StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase);

    private enum ScanState { Code, SingleQuote, DoubleQuote, Backtick, BlockComment }

    private static void SplitDelimitedScript(List<string> batches, string script)
    {
        var currentDelimiter = ";";
        var currentStatement = "";
        var state = ScanState.Code;

        foreach (var line in script.Split('\n'))
        {
            var trimmedLine = line.Trim();

            if (state == ScanState.Code && IsDelimiterCommand(line))
            {
                if (!string.IsNullOrWhiteSpace(currentStatement))
                {
                    batches.Add(currentStatement.Trim());
                    currentStatement = "";
                }

                currentDelimiter = trimmedLine.Substring(10).Trim();
                continue;
            }

            currentStatement += line + "\n";

            // The statement ends where a line ends in the delimiter -- unless that delimiter is inside a string, a
            // quoted identifier or a comment.
            var trimmedEnd = line.TrimEnd();
            var delimiterAt = trimmedEnd.EndsWith(currentDelimiter, StringComparison.Ordinal) ? trimmedEnd.Length - currentDelimiter.Length : -1;
            state = ScanLine(line, state, delimiterAt, out var delimiterIsCode);
            if (!delimiterIsCode) continue;

            currentStatement = currentStatement.Trim();
            if (currentStatement.EndsWith(currentDelimiter, StringComparison.Ordinal) && currentDelimiter != ";")
                currentStatement = currentStatement.Substring(0, currentStatement.Length - currentDelimiter.Length);
            if (!string.IsNullOrWhiteSpace(currentStatement)) batches.Add(currentStatement.Trim());
            currentStatement = "";
        }

        if (!string.IsNullOrWhiteSpace(currentStatement)) batches.Add(currentStatement.Trim());
    }

    // Carries the lexical state across one line, and reports whether the character at checkAt was outside every string,
    // quoted identifier and comment.
    private static ScanState ScanLine(string line, ScanState state, int checkAt, out bool codeAtCheck)
    {
        codeAtCheck = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (i == checkAt) codeAtCheck = state == ScanState.Code;
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            switch (state)
            {
                case ScanState.Code:
                    if (c == '\'') state = ScanState.SingleQuote;
                    else if (c == '"') state = ScanState.DoubleQuote;
                    else if (c == '`') state = ScanState.Backtick;
                    else if (c == '/' && next == '*') { state = ScanState.BlockComment; i++; }
                    else if (c == '#' || (c == '-' && next == '-' && (i + 2 >= line.Length || char.IsWhiteSpace(line[i + 2]))))
                        return state;
                    break;
                case ScanState.SingleQuote:
                case ScanState.DoubleQuote:
                    var quote = state == ScanState.SingleQuote ? '\'' : '"';
                    if (c == '\\') i++;
                    else if (c == quote && next == quote) i++;
                    else if (c == quote) state = ScanState.Code;
                    break;
                case ScanState.Backtick:
                    if (c == '`' && next == '`') i++;
                    else if (c == '`') state = ScanState.Code;
                    break;
                case ScanState.BlockComment:
                    if (c == '*' && next == '/') { state = ScanState.Code; i++; }
                    break;
            }
        }
        return state;
    }
}
