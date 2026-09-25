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

    /// <summary>
    /// The C# sources that BUILD SQL Server catalog queries as string literals. The sibling guard below
    /// scans these because the resource-based one structurally cannot: it enumerates manifest resources
    /// filtered to <c>.EndsWith(".sql")</c>, and a query assembled in C# is neither. v2.7.0 dropped the
    /// NOLOCK hint from every <c>.sql</c> script and stated the property as a fact, while 33 catalog
    /// reads in these three files kept it and the green guard said nothing.
    /// </summary>
    private static IEnumerable<(string Name, string Source)> CatalogBuildingCSharpSources()
    {
        // Walk up from the test binary to the repo root, then take the three product files by path.
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "could not locate the repository root from the test directory");

        foreach (var rel in new[]
                 {
                     Path.Join("Schema", "Utility", "MergeScriptHelper.cs"),
                     Path.Join("DataTongs", "DataTongs.cs"),
                     Path.Join("SchemaTongs", "SchemaTongs.cs"),
                 })
        {
            var full = Path.Join(dir!.FullName, rel);
            Assert.That(File.Exists(full), $"expected to scan '{rel}' but it is not there -- if it moved, "
                                           + "move this list with it rather than letting the guard go quiet");
            yield return (rel, File.ReadAllText(full));
        }
    }

    /// <summary>
    /// Every hand-declared <c>[SchemaProperty(Pattern = …)]</c> must reach the generated schema anchored.
    /// A JSON Schema pattern is a PARTIAL match, so an unanchored alternation is wrong in the permissive
    /// direction for every consumer that lacks SchemaSmith's own loader — Ajv in CI, and every editor the
    /// <c>$schema</c> feature was just built to serve. 21 of 25 declarations shipped unanchored; the
    /// generator now anchors centrally and this holds it there.
    /// </summary>
    [Test]
    public void EveryDeclaredSchemaPropertyPattern_ReachesTheSchemaAnchored()
    {
        var offenders = new List<string>();
        var checked_ = 0;
        foreach (var type in SchemaAsm.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var prop in type.GetProperties())
            {
                var attr = prop.GetCustomAttribute<Schema.Domain.SchemaPropertyAttribute>();
                if (attr == null || string.IsNullOrEmpty(attr.Pattern)) continue;
                checked_++;

                var emitted = Schema.Utility.SchemaGenerator.DeclaredPatternForTest(
                    attr.Pattern, attr.PatternIgnoreCase);
                if (!emitted.StartsWith('^') || !emitted.EndsWith('$'))
                    offenders.Add($"{type.Name}.{prop.Name}: {emitted}");
            }
        }

        Assert.That(checked_, Is.GreaterThan(15), "no declared patterns were found, so this proves nothing");
        Assert.That(offenders, Is.Empty,
            "A declared pattern reaches the generated schema unanchored. JSON Schema patterns are partial "
            + "matches, so 'NEVER|ALWAYS' accepts 'XNEVERY' and 'Y|N' accepts any string containing a Y or "
            + "an N -- permissive exactly where the schema is the only check a third-party editor has. "
            + "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Test]
    public void CaseInsensitivelyReadProperties_EmitAPatternThatAcceptsTheCasingsTheProductDoes()
    {
        // The two whose product-side reads were actually measured. MergeType compares OrdinalIgnoreCase
        // and RebuildPolicy.Mode upper-cases first, so both deploy from lower case while a case-sensitive
        // pattern failed --Validate on them: the linter contradicting the product.
        var mergeType = Schema.Utility.SchemaGenerator.DeclaredPatternForTest(
            "Insert|Insert/Update|Insert/Update/Delete", true);
        var mode = Schema.Utility.SchemaGenerator.DeclaredPatternForTest("NEVER|ALWAYS|THRESHOLD", true);

        Assert.Multiple(() =>
        {
            Assert.That(Regex.IsMatch("insert/update", mergeType), Is.True, "the product accepts it, so must the schema");
            Assert.That(Regex.IsMatch("Insert/Update", mergeType), Is.True);
            Assert.That(Regex.IsMatch("XInsertY", mergeType), Is.False, "and it must still be anchored");
            Assert.That(Regex.IsMatch("never", mode), Is.True, "the product upper-cases before comparing");
            Assert.That(Regex.IsMatch("XNEVERY", mode), Is.False);
        });
    }

    /// <summary>
    /// No member carries back-to-back <c>&lt;summary&gt;</c> blocks.
    /// <para>
    /// A doc comment separated from its member by an inserted member is silently adopted by the new one,
    /// so that member ends up with two summaries — the first describing something else — and the original
    /// with none. It compiles, it never throws, and the only symptom is a reader being told the wrong
    /// thing. Twelve sites existed when this was written: eleven from methods reordered away from their
    /// docs, and one from a fix that inserted three helpers anchored on a signature, which put them
    /// between that signature and its own comment.
    /// </para>
    /// <para>
    /// The guard is the deliverable, not the twelve edits: this catches the mistake at the moment it is
    /// made rather than at a downstream consumer's re-pack, which is how the twelfth was actually found.
    /// </para>
    /// </summary>
    [Test]
    public void NoMemberCarriesBackToBackSummaryBlocks()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "could not locate the repository root");

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var project in new[] { "Schema", "SchemaQuench", "SchemaTongs", "DataTongs", "SchemaShears" })
        {
            var root = new DirectoryInfo(Path.Join(dir!.FullName, project));
            if (!root.Exists) continue;
            foreach (var cs in root.GetFiles("*.cs", SearchOption.AllDirectories))
            {
                if (cs.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || cs.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                scanned++;
                var lines = File.ReadAllLines(cs.FullName);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("</summary>", StringComparison.Ordinal)) continue;
                    var j = i + 1;
                    while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                    if (j < lines.Length && lines[j].Contains("<summary>", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(cs.FullName)}:{i + 1}");
                }
            }
        }

        Assert.That(scanned, Is.GreaterThan(100), "almost no sources were scanned, so this proves nothing");
        Assert.That(offenders, Is.Empty,
            "A member carries two <summary> blocks, so the first one describes a different member and that "
            + "member has none. This does not throw and does not fail a build -- the only symptom is a "
            + "reader being told the wrong thing about the code in front of them. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    [Test]
    public void PaddingToleranceFollowsTheProduct_PerProperty_NotBlanket()
    {
        // Anchoring the patterns created one fresh instance of the defect it closed: RebuildPolicy.Mode is
        // read as (… ?? "NEVER").Trim().ToUpperInvariant(), so " NEVER " DEPLOYS, and an anchored pattern
        // rejected it. MergeType is read with a bare Equals(…, OrdinalIgnoreCase) and does NOT trim, so
        // " Insert " is genuinely invalid there. Both directions are pinned here because the tempting fix --
        // \s* on every pattern -- would make the linter accept what MergeType rejects, which is the same
        // error pointing the other way.
        var mode = Schema.Utility.SchemaGenerator.DeclaredPatternForTest(
            "NEVER|ALWAYS|THRESHOLD", true, allowPadding: true);
        var mergeType = Schema.Utility.SchemaGenerator.DeclaredPatternForTest(
            "Insert|Insert/Update|Insert/Update/Delete", true);

        Assert.Multiple(() =>
        {
            Assert.That(Regex.IsMatch(" NEVER ", mode), Is.True,
                "the product trims Mode before comparing, so padding deploys and the schema must accept it");
            Assert.That(Regex.IsMatch("never", mode), Is.True, "and case still folds");
            Assert.That(Regex.IsMatch(" XNEVERY ", mode), Is.False, "padding tolerance is not a licence to unanchor");
            Assert.That(Regex.IsMatch(" Insert ", mergeType), Is.False,
                "MergeType does NOT trim, so padded values are correctly still rejected -- tolerance is "
                + "per-property and measured, never blanket");
        });
    }

    [Test]
    public void NoCatalogReadBuiltInCSharpCarriesTheNolockHint()
    {
        // Same property as the script guard, different surface. Reads of the USER'S OWN data tables are
        // deliberately not matched: DataTongs extracts row data WITH (NOLOCK) on purpose, to avoid
        // blocking a production OLTP workload while it reads. That is a different decision from reading
        // the catalog dirty, and inverting it would change locking behaviour against live tables.
        var catalogRead = new Regex(
            @"(?:\bsys\.|\bINFORMATION_SCHEMA\.)\s*\[?\w+\]?\s+(?:AS\s+)?\w*\s*WITH\s*\(\s*NOLOCK",
            RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var (name, source) in CatalogBuildingCSharpSources())
        {
            scanned++;
            foreach (Match m in catalogRead.Matches(source))
                offenders.Add($"{name}: {Regex.Replace(m.Value, @"\s+", " ")}");
        }

        // Guards the premise, exactly as the sibling test does: a regex that matched nothing because
        // nothing was scanned would pass while proving nothing at all.
        Assert.That(scanned, Is.EqualTo(3), "the C# sources were not all scanned, so this proves nothing");

        Assert.That(offenders, Is.Empty,
            "A system-catalog read built in C# carries WITH (NOLOCK). This is the same hazard the script "
            + "guard covers and it is NOT covered by that guard, which only sees embedded .sql resources. "
            + "The sharpest case is MergeScriptHelper.GetKeyColumnsSqlServer: it picks a MERGE's KEY "
            + "COLUMNS from sys.indexes/sys.index_columns/sys.columns, so a dirty read that skips a row "
            + "produces a MERGE keyed on the wrong columns -- no error, wrong rows updated or deleted. "
            + "Reads of the user's own data tables are intentional and are not matched. Offenders:\n  "
            + string.Join("\n  ", offenders));
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
