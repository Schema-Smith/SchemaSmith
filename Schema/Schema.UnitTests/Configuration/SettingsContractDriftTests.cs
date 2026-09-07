// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Schema.Configuration;

namespace Schema.UnitTests.Configuration;

/// <summary>
/// The guard that makes <see cref="SettingsContract"/> a contract rather than a promise.
/// <para>A manifest of accepted keys that nothing checks is a second source of truth, and it drifts the
/// moment someone adds a <c>config["…"]</c> read and forgets to register it. This scans the product
/// sources for configuration reads and fails if any key is unregistered, so the contract cannot fall
/// behind the code without the suite going red.</para>
/// </summary>
[TestFixture]
public class SettingsContractDriftTests
{
    private static readonly string[] ProductProjects =
        ["Schema", "SchemaQuench", "SchemaTongs", "DataTongs", "SchemaShears"];

    // Known ways product code reaches a configuration value by name. Any indexer whose literal is
    // preceded by a `)` or an identifier covers config["k"], _config["k"], Resolve<...>()?["k"], and
    // the like; GetSection / GetValue / ReadProperties cover the call forms.
    private static readonly Regex ConfigRead =
        new(@"_?[Cc]onfig(?:uration)?(?:Root)?\s*\??\[\s*""(?<key>[^""]+)""\s*\]"
          + @"|IConfiguration(?:Root)?>\(\)\s*\??\[\s*""(?<key>[^""]+)""\s*\]"
          + @"|GetSection\(\s*""(?<key>[^""]+)""\s*\)"
          + @"|GetValue<[^>]+>\(\s*""(?<key>[^""]+)"""
          + @"|ReadProperties\([^,]+,\s*""(?<key>[^""]+)"""
          + @"|(?:ConfigBool|ReadFilterArray)\((?:[^,()]*,\s*)?""(?<key>[^""]+)""",
            RegexOptions.Compiled);

    // A namespaced key ("Target:Server") is unmistakable — it never occurs as a folder name or other
    // incidental string — so any literal equal to one is a config read whatever syntax surrounds it.
    // Bare keys ("Tables", "Schemas") are deliberately excluded: they collide with ordinary strings.
    private static readonly Regex AnyStringLiteral = new(@"""(?<key>[^""\\]*:[^""\\]*)""", RegexOptions.Compiled);

    // Documentation quotes config reads to explain them (this file does it too), so comments must not
    // be scanned or the contract would be asked to register examples.
    private static string StripComments(string source) =>
        string.Join(Environment.NewLine,
            source.Split('\n')
                  .Select(l => l.TrimStart())
                  .Select(l => l.StartsWith("//") || l.StartsWith('*') || l.StartsWith("/*") ? "" : l));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static IEnumerable<(string File, string Key)> ProductConfigReads(string root)
    {
        var contractKeys = SettingsContract.AllAcceptedKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in ScannableProductFiles(root))
        {
            var text = StripComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(root, file);

            // Interpolated or composed keys cannot be checked statically.
            var reads = ConfigRead.Matches(text)
                .Select(m => m.Groups["key"].Value)
                .Where(k => k.Length > 0 && !k.Contains('{'));

            // Any namespaced literal that IS a contract key, regardless of the syntax around it.
            var literals = AnyStringLiteral.Matches(text)
                .Select(m => m.Groups["key"].Value)
                .Where(contractKeys.Contains);

            foreach (var key in reads.Concat(literals))
                yield return (relative, key);
        }
    }

    private static IEnumerable<string> ScannableProductFiles(string root) =>
        ProductProjects
            .Select(project => Path.Join(root, project))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(IsScannable);

    private static bool IsScannable(string file) =>
        // Test projects live beneath the product project directories; they legitimately use
        // harness-only keys (SqlServer:Server, PostgreSQL:Password, …) that are not settings.
        !file.Contains("Tests", StringComparison.OrdinalIgnoreCase) &&
        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
        // The contract's own files necessarily spell the keys out.
        !file.EndsWith("SettingsKeys.cs") && !file.EndsWith("SettingsContract.cs");

    [Test]
    public void EveryConfigurationKeyReadByProductCode_IsRegisteredInTheContract()
    {
        var root = RepoRoot();
        Assert.That(root, Is.Not.Null, "could not locate the repository root from the test output directory");

        var accepted = SettingsContract.AllAcceptedKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unregistered = ProductConfigReads(root)
            .Where(r => !accepted.Contains(r.Key))
            .Where(r => !SettingsContract.IsOpenSection(r.Key))
            // A relative sub-key read from an already-scoped section (child.GetSection("Databases")).
            .Where(r => !IsScopedSubKey(r.Key))
            .Distinct()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.That(unregistered, Is.Empty,
            "These configuration keys are read by product code but are not registered in SettingsContract, " +
            "so the contract has drifted behind the code:" + Environment.NewLine +
            string.Join(Environment.NewLine, unregistered.Select(r => $"  {r.Key}   ({r.File})")));
    }

    // Sub-keys read relative to a section already resolved from a registered parent — e.g.
    // Target:TemplateTargets:<Template> then GetSection("Databases"). The parent is what the
    // contract registers; these are members of an open section.
    private static bool IsScopedSubKey(string key) =>
        !key.Contains(':') &&
        key is "Databases" or "Schemas" or "Tables" or "CreateIfMissing";

    /// <summary>
    /// The invariant that makes the scan exhaustive rather than best-effort.
    /// <para>A regex that hunts for config reads can only catch the shapes it knows; a read written
    /// some other way slips through and its key goes unguarded. Requiring every read to name a
    /// <see cref="SettingsKeys"/> constant inverts that: a raw literal is itself the failure, so
    /// there is no shape for an unregistered key to hide in.</para>
    /// </summary>
    [Test]
    public void NoProductCodeReadsConfigurationByStringLiteral()
    {
        var root = RepoRoot();
        Assert.That(root, Is.Not.Null);

        var literals = ProductConfigReads(root).Distinct().OrderBy(r => r.File).ThenBy(r => r.Key).ToList();

        Assert.That(literals, Is.Empty,
            "Configuration must be read through SettingsKeys constants, not string literals — a literal " +
            "is a key the contract cannot see. Register it in SettingsKeys and read through it:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, literals.Select(r => $"  \"{r.Key}\"   ({r.File})")));
    }

    /// <summary>
    /// The gap that let <c>DropEventsRemovedFromProduct</c> ship missing from the contract.
    /// <para><see cref="EveryConfigurationKeyReadByProductCode_IsRegisteredInTheContract"/> scans for
    /// string-LITERAL reads, but <see cref="NoProductCodeReadsConfigurationByStringLiteral"/> forces every
    /// read through a <see cref="SettingsKeys"/> constant — so a key read the sanctioned way
    /// (<c>ConfigBool(_config, SettingsKeys.DropEventsRemovedFromProduct)</c>) has no literal for the drift
    /// scan to catch, and nothing else asserted that a referenced constant is in the contract. A consumer
    /// validating a settings file against an incomplete contract rejects a perfectly valid setting — the
    /// contract is invalid. This closes that direction: every constant product code actually reads must be
    /// registered.</para>
    /// </summary>
    [Test]
    public void EveryConfigurationKeyReferencedByProductCode_IsRegisteredInTheContract()
    {
        var root = RepoRoot();
        Assert.That(root, Is.Not.Null);

        var accepted = SettingsContract.AllAcceptedKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unregistered = ReferencedKeyValues(root)
            .Where(k => !accepted.Contains(k))
            .Where(k => !SettingsContract.IsOpenSection(k))
            .Where(k => !IsScopedSubKey(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.That(unregistered, Is.Empty,
            "These configuration keys are read by product code through a SettingsKeys constant but are not " +
            "registered in SettingsContract, so the contract is incomplete — a consumer validating a settings " +
            "file against it would reject a valid setting:" + Environment.NewLine +
            string.Join(Environment.NewLine, unregistered.Select(k => "  " + k)));
    }

    /// <summary>
    /// The other direction: a registered key nothing reads is dead weight that quietly accumulates,
    /// and a contract carrying settings the tools ignore is exactly the unreliable promise this
    /// mechanism exists to avoid.
    /// </summary>
    [Test]
    public void EveryContractKeyIsActuallyReadByProductCode()
    {
        var root = RepoRoot();
        Assert.That(root, Is.Not.Null);

        var referenced = ReferencedKeyValues(root);
        var dead = SettingsContract.AllAcceptedKeys()
            .Where(k => !referenced.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.That(dead, Is.Empty,
            "These keys are registered in the contract but no product code reads them, so the contract " +
            "promises settings the tools ignore:" + Environment.NewLine +
            string.Join(Environment.NewLine, dead.Select(k => "  " + k)));
    }

    // The literal values behind every SettingsKeys constant that product code actually references.
    private static HashSet<string> ReferencedKeyValues(string root)
    {
        var byName = KeyConstantsByPath();
        var reference = new Regex(@"SettingsKeys(?:\.\w+)+", RegexOptions.Compiled);

        return ScannableProductFiles(root)
            .SelectMany(file => reference.Matches(StripComments(File.ReadAllText(file))))
            .Select(m => byName.TryGetValue(m.Value, out var value) ? value : null)
            .Where(value => value != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // "SettingsKeys.Target.Server" -> "Target:Server", by reflection over the constants themselves.
    private static Dictionary<string, string> KeyConstantsByPath()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Walk(Type type, string prefix)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                map[$"{prefix}.{field.Name}"] = (string)field.GetRawConstantValue();
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
                Walk(nested, $"{prefix}.{nested.Name}");
        }

        Walk(typeof(SettingsKeys), nameof(SettingsKeys));
        return map;
    }

    [Test]
    public void ContractKeysAreWellFormed()
    {
        foreach (var key in SettingsContract.AllAcceptedKeys())
        {
            Assert.That(key.Trim(), Is.EqualTo(key), $"'{key}' has surrounding whitespace");
            Assert.That(key, Is.Not.Empty);
            Assert.That(key, Does.Not.StartWith(":"), $"'{key}' starts with a separator");
            Assert.That(key, Does.Not.EndWith(":"), $"'{key}' ends with a separator");
        }
    }

    [Test]
    public void EveryToolHasKeys()
    {
        foreach (SettingsTool tool in Enum.GetValues<SettingsTool>())
            Assert.That(SettingsContract.AcceptedKeys(tool), Is.Not.Empty, $"{tool} registered no settings keys");
    }
}
