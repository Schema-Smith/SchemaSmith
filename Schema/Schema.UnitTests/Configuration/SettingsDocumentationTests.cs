// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Schema.Configuration;

namespace Schema.UnitTests.Configuration;

/// <summary>
/// Every setting a tool accepts is named somewhere in the end-user reference, so a user can find what it does. A key
/// counts as named when the reference writes it as code (`Target:Server`, `Server`, `Target__Server`) or as a JSON key
/// in an example ("Server"). The reference pages are on the unit-tested side of .github/ci-tested-paths.txt, so an edit
/// to one runs these tests rather than the integration matrix.
/// </summary>
[TestFixture]
public class SettingsDocumentationTests
{
    [Test]
    public void EveryAcceptedSetting_IsNamedInTheReference()
    {
        var reference = Path.Join(RepoSources.Root, "docs", "end-user", "reference");
        var text = string.Join("\n", Directory.GetFiles(reference, "*.md").Select(File.ReadAllText));
        Assert.That(text, Has.Length.GreaterThan(10000), "the reference pages were read");

        var undocumented = SettingsContract.AllAcceptedKeys()
            .Where(key =>
            {
                var leaf = key.Split(':')[^1];
                return !text.Contains($"`{key}`", StringComparison.Ordinal) && !text.Contains($"`{leaf}`", StringComparison.Ordinal)
                       && !text.Contains($"\"{leaf}\"", StringComparison.Ordinal) && !text.Contains($"`{key.Replace(":", "__")}`", StringComparison.Ordinal);
            })
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.That(undocumented, Is.Empty, "settings a tool accepts that no reference page names");
    }
}
