// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Schema.Configuration;

namespace Schema.UnitTests.Configuration;

/// <summary>
/// Pins the committed <c>settings-contract.json</c> to the contract in code.
/// <para>The artifact exists so the other side of the boundary has something concrete to check
/// against — an external parity gate reads it (or calls <see cref="SettingsContract"/> directly)
/// instead of transcribing keys by hand, which is the half of that gate that would otherwise rot.
/// It also makes a settings change visible in a PR diff rather than buried in code.</para>
/// <para>A published artifact nobody verifies is the same unreliable promise as an unverified
/// contract, so this test regenerates it from the code and fails if the committed copy differs.</para>
/// </summary>
[TestFixture]
public class SettingsContractArtifactTests
{
    private static string ArtifactPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir == null ? null : Path.Join(dir.FullName, "Schema", "Configuration", "settings-contract.json");
    }

    [Test]
    public void CommittedArtifactMatchesTheContractInCode()
    {
        var path = ArtifactPath();
        Assert.That(path, Is.Not.Null, "could not locate the repository root");

        var expected = SettingsContract.ToJson() + Environment.NewLine;
        var actual = File.Exists(path) ? File.ReadAllText(path) : null;

        if (Normalize(actual) == Normalize(expected)) return;

        // Rewrite it so the fix is "review the diff and commit", not "hand-edit 100 keys".
        File.WriteAllText(path, expected);
        Assert.Fail(File.Exists(path) && actual != null
            ? "settings-contract.json was out of date with SettingsContract and has been regenerated. " +
              "Review the diff and commit it."
            : "settings-contract.json was missing and has been generated. Review it and commit it.");
    }

    private static string Normalize(string s) => s?.Replace("\r\n", "\n").TrimEnd();

    [Test]
    public void ArtifactRoundTripsToTheSameKeySets()
    {
        // The artifact is only useful to a consumer if what it publishes is what the tools enforce.
        using var doc = JsonDocument.Parse(SettingsContract.ToJson());
        var tools = doc.RootElement.GetProperty("tools");

        foreach (SettingsTool tool in Enum.GetValues<SettingsTool>())
        {
            var published = tools.GetProperty(tool.ToString())
                .EnumerateArray().Select(e => e.GetString()).ToList();

            Assert.That(published, Is.EquivalentTo(SettingsContract.AcceptedKeys(tool)),
                $"published keys for {tool} differ from the enforced set");
        }

        Assert.That(doc.RootElement.GetProperty("openSections").EnumerateArray().Select(e => e.GetString()),
            Is.EquivalentTo(SettingsContract.OpenSectionNames()));
        Assert.That(doc.RootElement.GetProperty("arrayKeys").EnumerateArray().Select(e => e.GetString()),
            Is.EquivalentTo(SettingsContract.ArrayKeyNames()));
    }
}
