// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Schema.Capabilities;

namespace Schema.UnitTests.Scripts;

/// <summary>
/// Mechanical guards over the shipped MySQL and MariaDB scripts.
/// </summary>
[TestFixture]
public class MySqlScriptGuardTests
{
    /// <summary>
    /// Every working table the scripts create declares its character set. Without one it takes the target
    /// database's default, and in a latin1 or utf8mb3 database the first comparison between it and another working
    /// table, or a catalog column, fails with "Illegal mix of collations" -- every deploy to such a database, and
    /// nothing a deploy into a utf8mb4 database can show. A table-level DEFAULT CHARSET, or a character set on each
    /// character column, satisfies it.
    /// </summary>
    [Test]
    public void EveryWorkingTable_DeclaresItsCharacterSet()
    {
        var offenders = new List<string>();
        var tablesChecked = 0;
        foreach (var (name, sql) in Scripts())
        {
            var code = Regex.Replace(sql, @"--[^\r\n]*", string.Empty);
            foreach (Match start in Regex.Matches(code, @"CREATE\s+(?:TEMPORARY\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(`?\w+`?)\s*\(",
                         RegexOptions.IgnoreCase))
            {
                var close = ClosingParen(code, start.Index + start.Length - 1);
                if (close < 0) continue;
                tablesChecked++;
                var body = code[(start.Index + start.Length)..close];
                var options = code[(close + 1)..].Split(';')[0];
                if (Regex.IsMatch(options, @"\b(?:CHARSET|CHARACTER\s+SET)\b", RegexOptions.IgnoreCase)) continue;

                var bare = Regex.Split(body, @",(?![^()]*\))")
                    .Select(c => Regex.Replace(c, @"\s+", " ").Trim())
                    .Where(c => Regex.IsMatch(c, @"^`?\w+`? (?:VARCHAR|CHAR|TINYTEXT|TEXT|MEDIUMTEXT|LONGTEXT|ENUM|SET)\b",
                                    RegexOptions.IgnoreCase)
                                && !Regex.IsMatch(c, @"\b(?:CHARSET|CHARACTER\s+SET|COLLATE)\b", RegexOptions.IgnoreCase))
                    .ToList();
                offenders.AddRange(bare.Select(c => $"{name}: {start.Groups[1].Value} {c}"));
            }
        }

        Assert.That(tablesChecked, Is.GreaterThan(100), "too few working tables found; the scan is broken");
        Assert.That(offenders, Is.Empty,
            "A working table character column has no character set, so it takes the target database's default and "
            + "a deploy to a latin1 or utf8mb3 database fails comparing with it. Declare DEFAULT CHARSET=utf8mb4 on "
            + "the table:\n  " + string.Join("\n  ", offenders));
    }

    private static int ClosingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static IEnumerable<(string Name, string Sql)> Scripts()
    {
        var asm = typeof(Capability).Assembly;
        foreach (var resource in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".MySQL.", StringComparison.Ordinal) || n.Contains(".MariaDb.", StringComparison.Ordinal))
                     .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            yield return (resource, reader.ReadToEnd());
        }
    }
}
