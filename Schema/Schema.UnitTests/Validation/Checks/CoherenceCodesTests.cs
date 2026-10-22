// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// CoherenceCheck.Codes is what a caller reads instead of keeping its own copy of the levels, so it is held to the
/// check itself: every code the check declares, and every level the source reports it at.
/// </summary>
[TestFixture]
public class CoherenceCodesTests
{
    private static Dictionary<string, string> DeclaredCodes() =>
        typeof(CoherenceCheck).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Code"))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Test]
    public void Codes_ListsEveryCodeTheCheckDeclares_AndNoOther()
    {
        var declared = DeclaredCodes().Values.ToList();
        Assert.That(declared, Has.Count.GreaterThan(50), "reflection found the check's code constants");
        Assert.That(CoherenceCheck.Codes.Keys, Is.EquivalentTo(declared));
    }

    // Some emission sites take the level or the code from a variable, so the source cannot say what they report.
    private static readonly Dictionary<string, Severity[]> NotLiteral = new()
    {
        ["SS-FK-006"] = [Severity.Error, Severity.Warning],
        ["SS-PART-009"] = [Severity.Error, Severity.Warning],
        ["SS-ENUM-001"] = [Severity.Error],
        ["SS-SEQ-001"] = [Severity.Error],
        ["SS-DOM-001"] = [Severity.Error],
    };

    [Test]
    public void Codes_CarryTheLevelsTheSourceReports()
    {
        var source = File.ReadAllText(Path.Join(RepoSources.Root, "Schema", "Validation", "Checks", "CoherenceCheck.cs"));
        var reported = Regex.Matches(source, @"new Finding\(\s*Severity\.(\w+)\s*,\s*(\w+)")
            .GroupBy(m => m.Groups[2].Value, m => System.Enum.Parse<Severity>(m.Groups[1].Value))
            .ToDictionary(g => g.Key, g => g.Distinct().OrderBy(s => s).ToArray());
        var declared = DeclaredCodes();

        Assert.Multiple(() =>
        {
            foreach (var (constant, code) in declared)
            {
                var expected = NotLiteral.TryGetValue(code, out var held) ? held
                    : reported.TryGetValue(constant, out var found) ? found : [];
                var listed = CoherenceCheck.Codes.TryGetValue(code, out var levels) ? levels.OrderBy(s => s).ToArray() : [];
                Assert.That(listed, Is.EqualTo(expected), $"{code} ({constant})");
            }
        });
    }
}
