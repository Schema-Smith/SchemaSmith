// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Schema.Capabilities;

namespace Schema.UnitTests.Scripts;

/// <summary>
/// Mechanical guards over the shipped scripts for a value written into SQL they generate: a name or other text spliced
/// into a generated string literal must be escaped for that literal, and one spliced into a generated message must be
/// escaped for the message as well. Each rule names the form the sweep settled on; a new site written the old way fails
/// here with its file and line.
/// </summary>
[TestFixture]
public class IdentifierSafetyScriptGuardTests
{
    private static readonly Assembly SchemaAsm = typeof(Capability).Assembly;

    // Splices whose value cannot hold a quote, each with the reason.
    private static readonly (string Pattern, string Reason)[] SafeSplices =
    [
        (@"^@v_Bad\w+", "an error message built for RAISERROR('%s'), not generated SQL; the quotes around it are text"),
    ];

    private static IEnumerable<(string Name, string Code)> Scripts(params string[] engines)
    {
        foreach (var resource in SchemaAsm.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .Where(n => engines.Any(e => n.Contains($".{e}.", StringComparison.Ordinal))))
        {
            using var stream = SchemaAsm.GetManifestResourceStream(resource);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            // Comments explain the old forms by name; only code is scanned. Positions are kept for the line numbers.
            yield return (resource, Regex.Replace(reader.ReadToEnd(), @"--[^\r\n]*", m => new string(' ', m.Length)));
        }
    }

    private static int LineOf(string text, int index) => 1 + text.AsSpan(0, index).Count('\n');

    private static string Excerpt(string text, int index) => Regex.Replace(text.Substring(index, Math.Min(70, text.Length - index)), @"\s+", " ");

    private static bool IsSafeSplice(string expression) => SafeSplices.Any(s => Regex.IsMatch(expression, s.Pattern));

    // A literal that ends in an escaped quote (exactly three quotes), then the concatenation: the start of a value spliced
    // into a string literal of the generated SQL. Four quotes are a literal holding one quote, which may as well close a
    // generated literal as open one, so they are not judged here.
    private static List<string> UnescapedSplices(string[] engines, string concatenation, string requiredStart, ref int sitesChecked)
    {
        var offences = new List<string>();
        var pattern = new Regex(@"(?<!')N?'''(?!')\s*" + concatenation + @"\s*");
        foreach (var (name, code) in Scripts(engines))
            foreach (Match m in pattern.Matches(code))
            {
                sitesChecked++;
                var expression = code[m.Index..][m.Length..];
                if (Regex.IsMatch(expression, "^(" + requiredStart + ")", RegexOptions.IgnoreCase) || IsSafeSplice(expression)) continue;
                offences.Add($"{name}:{LineOf(code, m.Index)}: {Excerpt(code, m.Index)}");
            }
        return offences;
    }

    // The values spliced at the top level of a generated message: from just inside the message (in the outer literal)
    // to the generated quote that ends it. A splice is the outer literal closing, the concatenation operator, and an
    // expression up to the operator that reopens the literal; quotes and parentheses inside the expression are its own.
    private static IEnumerable<int> MessageSplices(string code, int start, string concatenation, Regex messageEnd)
    {
        var i = start;
        while (i < code.Length)
        {
            if (code[i] != '\'') { i++; continue; }
            if (i + 1 < code.Length && code[i + 1] == '\'')
            {
                if (messageEnd.Match(code, i).Success) yield break;
                i += 2;
                continue;
            }
            // The outer literal closes: either a splice follows, or the generated text ends here.
            var op = Regex.Match(code[(i + 1)..], @"^\s*" + Regex.Escape(concatenation) + @"\s*");
            if (!op.Success) yield break;
            var expressionStart = i + 1 + op.Length;
            yield return expressionStart;
            i = SkipExpression(code, expressionStart, concatenation);
        }
    }

    // From the start of a spliced expression to just inside the literal it is joined back to.
    private static int SkipExpression(string code, int i, string concatenation)
    {
        var depth = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '\'')
            {
                for (i++; i < code.Length; i++)
                    if (code[i] == '\'')
                    {
                        if (i + 1 < code.Length && code[i + 1] == '\'') { i++; continue; }
                        break;
                    }
                i++;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(code, i, concatenation, 0, concatenation.Length) == 0)
            {
                var reopen = Regex.Match(code[(i + concatenation.Length)..], @"^\s*N?'");
                if (reopen.Success) return i + concatenation.Length + reopen.Length;
            }
            i++;
        }
        return i;
    }

    /// <summary>
    /// SS-2: a value in a generated RAISERROR message is escaped for the literal and for RAISERROR's format, the
    /// REPLACE(REPLACE(x, '''', ''''''), '%', '%%') form. A quote ended the message mid-name; a percent sign was read as a
    /// placeholder.
    /// </summary>
    [Test]
    public void EveryValueInAGeneratedRaiseErrorMessage_IsEscapedForTheLiteralAndTheFormat()
    {
        var offences = new List<string>();
        var messages = 0;
        var end = new Regex(@"\G''\s*,\s*\d+\s*,\s*\d+");
        foreach (var (name, code) in Scripts("SqlServer"))
            foreach (Match m in Regex.Matches(code, @"RAISERROR\(N?''"))
            {
                messages++;
                foreach (var splice in MessageSplices(code, m.Index + m.Length, "+", end))
                {
                    var expression = code[splice..];
                    if (expression.StartsWith("REPLACE(REPLACE(", StringComparison.Ordinal) || IsSafeSplice(expression)) continue;
                    offences.Add($"{name}:{LineOf(code, splice)}: {Excerpt(code, splice)}");
                }
            }

        Assert.That(messages, Is.GreaterThan(100), "too few generated messages found; the scan is broken");
        Assert.That(offences, Is.Empty, "use REPLACE(REPLACE(x, '''', ''''''), '%', '%%'):" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    /// <summary>SS-3: a value spliced into a string literal of generated SQL Server SQL is escaped with REPLACE(x, '''', '''''').</summary>
    [Test]
    public void EveryValueSplicedIntoAGeneratedSqlServerLiteral_IsEscaped()
    {
        var sites = 0;
        var offences = UnescapedSplices(["SqlServer"], @"\+", "REPLACE\\(", ref sites);
        Assert.That(sites, Is.GreaterThan(100), "too few splices found; the scan is broken");
        Assert.That(offences, Is.Empty, "use REPLACE(x, '''', ''''''):" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// SS-4: a catalog lookup names the object by its quoted parts, and a rename's new name is escaped for the literal it
    /// is written into.
    /// </summary>
    [Test]
    public void EverySqlServerObjectLookupAndRename_QuotesItsNames()
    {
        var offences = new List<string>();
        foreach (var (name, code) in Scripts("SqlServer"))
        {
            foreach (Match m in Regex.Matches(code, @"OBJECT_ID\(\s*\w*\.?TABLE_SCHEMA\s*\+", RegexOptions.IgnoreCase))
                offences.Add($"{name}:{LineOf(code, m.Index)}: {Excerpt(code, m.Index)}  -- use OBJECT_ID(QUOTENAME(schema) + '.' + QUOTENAME(name))");
            foreach (Match m in Regex.Matches(code, @"sp_rename\s+N?'''\s*\+(?>\s*)(?!REPLACE\()", RegexOptions.IgnoreCase))
                offences.Add($"{name}:{LineOf(code, m.Index)}: {Excerpt(code, m.Index)}  -- use REPLACE(x, '''', '''''')");
        }
        Assert.That(offences, Is.Empty, string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// PG-1: a generated RAISE NOTICE takes its values as % arguments; spliced into the message text, a percent sign in a
    /// name was read as a placeholder.
    /// </summary>
    [Test]
    public void NoValueIsSplicedIntoAGeneratedRaiseNoticeMessage()
    {
        var offences = new List<string>();
        var messages = 0;
        var end = new Regex(@"\G''\s*[,;]");
        foreach (var (name, code) in Scripts("PostgreSQL"))
            foreach (Match m in Regex.Matches(code, @"RAISE NOTICE ''"))
            {
                messages++;
                offences.AddRange(MessageSplices(code, m.Index + m.Length, "||", end)
                    .Select(splice => $"{name}:{LineOf(code, splice)}: {Excerpt(code, splice)}"));
            }

        Assert.That(messages, Is.GreaterThan(50), "too few generated notices found; the scan is broken");
        Assert.That(offences, Is.Empty, "pass the value as a % argument:" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    /// <summary>PG-2: a value spliced into a string literal of generated PostgreSQL SQL is escaped.</summary>
    [Test]
    public void EveryValueSplicedIntoAGeneratedPostgreSqlLiteral_IsEscaped()
    {
        var sites = 0;
        var offences = UnescapedSplices(["PostgreSQL"], @"\|\|", @"REPLACE\(|quote_literal\(", ref sites);
        Assert.That(sites, Is.GreaterThan(50), "too few splices found; the scan is broken");
        Assert.That(offences, Is.Empty, "use REPLACE(x, '''', '''''') or quote_literal(x):" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// MY-1: a value written into generated MySQL or MariaDB SQL as a literal goes through QUOTE(), which escapes a
    /// backslash as well as a quote; doubling the quotes alone left the backslash to be read as an escape.
    /// </summary>
    [Test]
    public void EveryValueWrittenAsAGeneratedMySqlLiteral_GoesThroughQuote()
    {
        var offences = Scripts("MySQL", "MariaDb")
            .SelectMany(s => Regex.Matches(s.Code, @"(?<!')'''(?!')\s*,\s*REPLACE\(")
                .Select(m => $"{s.Name}:{LineOf(s.Code, m.Index)}: {Excerpt(s.Code, m.Index)}"))
            .ToList();
        Assert.That(offences, Is.Empty, "use ', QUOTE(x), ':" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }
}
