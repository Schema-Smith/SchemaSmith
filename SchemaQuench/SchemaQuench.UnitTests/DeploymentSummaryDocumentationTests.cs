// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Holds the deployment summary's reference page to the published schema, in both directions: a property the writer emits
/// that the page never names is undocumented, and a key a field table names that the schema lacks is a promise the writer
/// does not keep.
/// </summary>
[TestFixture]
public class DeploymentSummaryDocumentationTests
{
    private static string Page() =>
        File.ReadAllText(Path.Join(DeploymentSummarySchemaTests.RepoRoot(), "docs", "end-user", "reference", "deployment-summary-report.md"));

    private static HashSet<string> SchemaPropertyNames()
    {
        var schema = DeploymentSummarySchema.Generate();
        var objects = new[] { schema }.Concat(((JObject)schema["definitions"]!).Properties().Select(p => (JObject)p.Value));
        return objects.SelectMany(o => ((JObject)o["properties"]!).Properties().Select(p => p.Name)).ToHashSet();
    }

    // The first cell of every row of a table whose header's first column is "Key".
    private static List<string> FieldTableKeys(string page, out int tables)
    {
        var keys = new List<string>();
        tables = 0;
        var inKeyTable = false;
        foreach (var line in page.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith('|')) { inKeyTable = false; continue; }
            var first = line.Split('|')[1].Trim();
            if (first == "Key") { inKeyTable = true; tables++; continue; }
            if (!inKeyTable || first.StartsWith("---")) continue;
            keys.AddRange(Regex.Matches(first, "`([^`]+)`").Select(m => m.Groups[1].Value.Replace("[]", "").Split('.').Last()));
        }
        return keys;
    }

    [Test]
    public void EveryPropertyTheSummaryCarries_IsNamedOnTheReferencePage()
    {
        var page = Page();
        var names = SchemaPropertyNames();
        Assert.That(names, Has.Count.GreaterThan(80), "the schema walk found too few properties to be checking anything");

        // Named as itself, as an array (`slots[]`) or at the end of a path (`downgrades.feature`).
        var missing = names.Where(n => !Regex.IsMatch(page, $@"`([\w\[\]]*\.)?{Regex.Escape(n)}(\[\])?`")).OrderBy(n => n).ToList();

        Assert.That(missing, Is.Empty, "properties Summary.json carries that deployment-summary-report.md never names");
    }

    [Test]
    public void EveryKeyTheFieldTablesName_IsAPropertyOfTheSummary()
    {
        var keys = FieldTableKeys(Page(), out var tables);
        Assert.That(tables, Is.GreaterThanOrEqualTo(10), "field tables found on the page");
        Assert.That(keys, Has.Count.GreaterThan(50), "keys found in the page's field tables");

        var names = SchemaPropertyNames();
        var unknown = keys.Where(k => !names.Contains(k)).Distinct().OrderBy(k => k).ToList();

        Assert.That(unknown, Is.Empty, "keys the reference page documents that Summary.json does not carry");
    }
}
