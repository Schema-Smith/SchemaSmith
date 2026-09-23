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
/// Mechanical guards over the shipped SQL Server scripts, for two properties v2.7.0 states as facts and
/// nothing enforced.
/// <para>
/// Both exist because the failure they prevent is SILENT. A re-added <c>NOLOCK</c> does not throw: a
/// dirty read can skip a row as easily as duplicate one, and a skipped row reads as an object that needs
/// creating — the wrong DDL, at exit 0. A widened identifier column does not throw either; it makes the
/// correlated joins unindexable again and the deploy merely gets slow, which no assertion notices.
/// </para>
/// </summary>
[TestFixture]
public class SqlServerScriptGuardTests
{
    private static readonly Assembly SchemaAsm = typeof(Capability).Assembly;

    private static IEnumerable<(string Name, string Sql)> SqlServerScripts()
    {
        foreach (var resource in SchemaAsm.GetManifestResourceNames()
                     .Where(n => n.Contains(".SqlServer.", StringComparison.Ordinal))
                     .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = SchemaAsm.GetManifestResourceStream(resource);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            yield return (resource, reader.ReadToEnd());
        }
    }

    /// <summary>
    /// Strip line comments before scanning. These scripts explain themselves in prose, and several of
    /// them discuss the NOLOCK removal by name — a comment saying "no longer read WITH (NOLOCK)" must not
    /// be read as a catalog read that carries the hint.
    /// </summary>
    private static string WithoutLineComments(string sql) =>
        Regex.Replace(sql, @"--[^\r\n]*", string.Empty);

    /// <summary>
    /// The body of each <c>CREATE TABLE #X (...)</c>, so a scan can look at columns that are actually
    /// STORED rather than at every place the same identifier name is spelled.
    /// </summary>
    private static IEnumerable<string> CreateTableBlocks(string sql)
    {
        foreach (Match start in Regex.Matches(sql, @"CREATE\s+TABLE\s+#\w+\s*\(", RegexOptions.IgnoreCase))
        {
            var depth = 0;
            for (var i = start.Index + start.Length - 1; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')' && --depth == 0)
                {
                    yield return sql[(start.Index)..(i + 1)];
                    break;
                }
            }
        }
    }

    [Test]
    public void NoCatalogReadCarriesTheNolockHint()
    {
        // Session-private temp tables and SchemaSmith's own bookkeeping tables are deliberately out of
        // scope -- the claim is about the SYSTEM CATALOG, which concurrent DDL rewrites underneath a
        // scan. A #temp table cannot be read by another session at all, so a dirty read of one cannot
        // return a row twice.
        var catalogRead = new Regex(
            @"(?:\bsys\.|\bINFORMATION_SCHEMA\.)\s*\[?\w+\]?\s+(?:AS\s+)?\w*\s*WITH\s*\(\s*NOLOCK",
            RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var (name, sql) in SqlServerScripts())
        {
            scanned++;
            foreach (Match m in catalogRead.Matches(WithoutLineComments(sql)))
                offenders.Add($"{name}: {Regex.Replace(m.Value, @"\s+", " ")}");
        }

        // Guards the premise: a regex that matched nothing because nothing was scanned would pass while
        // proving nothing at all.
        Assert.That(scanned, Is.GreaterThan(10), "no SQL Server scripts were scanned, so this proves nothing");

        Assert.That(offenders, Is.Empty,
            "A system-catalog read carries WITH (NOLOCK) again. A dirty read of the catalog during a "
            + "deploy can return the same row twice OR SKIP ONE, and a skipped column or index reads as "
            + "one that needs creating -- so this emits the wrong DDL quietly, at exit 0, rather than "
            + "failing. Reads of temp tables and of SchemaSmith's own bookkeeping tables are fine and "
            + "are not matched here. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Test]
    public void WorkingSetIdentifierColumnsStayNarrowEnoughToIndex()
    {
        // NVARCHAR(MAX) cannot participate in an index key, so the correlated joins over the working set
        // degrade to LOB scans -- which is what made deciding whether each column was new cost 19.5s of
        // a parse. Narrowing them is also bounded from ABOVE: SQL Server's 900-byte index key limit is
        // 450 NVARCHAR characters, and a multi-column key has to fit inside it, which is how a
        // NVARCHAR(400) parent-lookup key produced a "maximum key length" warning on every deploy.
        var parse = SqlServerScripts().Single(s => s.Name.EndsWith("ParseTableJsonIntoTempTables.sql", StringComparison.Ordinal)).Sql;

        // Only the CREATE TABLE blocks. The same identifiers appear in OPENJSON ... WITH clauses, which
        // declare the SHRED's output columns rather than anything stored -- those are never indexed, so
        // holding them to a key-length limit would fail the test on code that cannot have the problem.
        var identifier = new Regex(
            @"\[(Schema|Name|TableName|ColumnName|IndexName|KeySchema|KeyTableName|OldName)\]\s+NVARCHAR\((MAX|\d+)\)",
            RegexOptions.IgnoreCase);

        var matches = CreateTableBlocks(parse).SelectMany(b => identifier.Matches(b)).ToList();
        Assert.That(matches, Is.Not.Empty, "no identifier columns found, so this proves nothing");

        var tooWide = matches
            .Select(m => (Column: m.Groups[1].Value, Width: m.Groups[2].Value))
            .Where(c => string.Equals(c.Width, "MAX", StringComparison.OrdinalIgnoreCase)
                        || int.Parse(c.Width) > 450)
            .Select(c => $"[{c.Column}] NVARCHAR({c.Width})")
            .Distinct()
            .ToList();

        Assert.That(tooWide, Is.Empty,
            "A working-set identifier column is too wide to take part in an index key. NVARCHAR(MAX) "
            + "cannot be indexed at all, and anything over 450 characters exceeds the 900-byte key limit "
            + "once combined. Neither fails a deploy -- the first makes the correlated joins LOB scans "
            + "and the second logs a maximum-key-length warning on every run: " + string.Join(", ", tooWide));
    }
}
