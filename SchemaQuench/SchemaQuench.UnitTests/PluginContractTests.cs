// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.Configuration;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Holds the Claude Code plugin's prose to the tools it operates: a skill may name only switches and settings the tools
/// accept, its exit-code table lists the codes the reference does, its version is the tools', and it carries no MCP
/// server, hooks or agents. Only deploy may show a SchemaQuench command that changes a database. Whether an instruction
/// crosses into authoring package content cannot be tested and stays a review duty.
/// </summary>
[TestFixture]
public class PluginContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Plugin => Path.Join(Root(), "plugin");

    private static IEnumerable<string> Skills() => Directory.GetDirectories(Path.Join(Plugin, "skills")).Select(Path.GetFileName).Order();

    private static string SkillText(string skill) => File.ReadAllText(Path.Join(Plugin, "skills", skill, "SKILL.md"));

    private static IEnumerable<(string File, string Text)> AllProse() =>
        Directory.GetFiles(Plugin, "*.md", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(Plugin, f), File.ReadAllText(f)));

    [Test]
    public void TheEightSkills_EachNamedForItsFolder_WithADescription()
    {
        Assert.That(Skills(), Is.EqualTo(new[] { "ci", "connect", "deploy", "drift", "explain-run", "extract", "install", "preview" }));
        foreach (var skill in Skills())
        {
            var front = Regex.Match(SkillText(skill), @"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
            Assert.That(front.Success, Is.True, $"{skill}: frontmatter");
            Assert.That(Regex.Match(front.Groups[1].Value, @"^name:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim(), Is.EqualTo(skill));
            Assert.That(Regex.IsMatch(front.Groups[1].Value, @"^description:\s*\S", RegexOptions.Multiline), Is.True, $"{skill}: description");
        }
    }

    private static HashSet<string> KnownSwitches()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LogPath", "ConfigFile", "ConnectionString" };
        known.UnionWith((string[])typeof(Program).GetField("KnownArguments", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
        known.UnionWith((string[])typeof(CommandLineParser).GetField("CommonBareFlags", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
        known.UnionWith(SettingsContract.AllAcceptedKeys());
        known.Add("WriteSummarySchema");
        known.Add("Key"); // the prose writes --Key:value for any setting
        return known;
    }

    [Test]
    public void EverySwitchTheProseNames_IsOneTheToolsAccept()
    {
        var known = KnownSwitches();
        var unknown = AllProse()
            .SelectMany(p => Regex.Matches(p.Text, @"(?<![\w-])--([A-Za-z]\w*)").Select(m => (p.File, Switch: m.Groups[1].Value)))
            // A switch's name ends at its first ':'; for a nested setting that name is its section.
            .Where(s => !known.Contains(s.Switch) && !known.Any(k => k.StartsWith(s.Switch + ":", StringComparison.OrdinalIgnoreCase)))
            .Select(s => $"{s.File}: --{s.Switch}")
            .Distinct().ToList();

        Assert.That(unknown, Is.Empty);
    }

    [Test]
    public void EverySettingTheProseNames_IsOneTheToolsAccept()
    {
        var accepted = new HashSet<string>(SettingsContract.AllAcceptedKeys(), StringComparer.OrdinalIgnoreCase);
        var named = AllProse().SelectMany(p =>
            Regex.Matches(p.Text, @"`((?:Target|Source|Product|Template):[A-Za-z:<>\-]+|SchemaPackagePath|KindleTheForge)`").Select(m => (p.File, Key: m.Groups[1].Value))
                .Concat(Regex.Matches(p.Text, @"SmithySettings_([A-Za-z_]+)").Select(m => (p.File, Key: m.Groups[1].Value.Replace("__", ":")))))
            .ToList();
        Assert.That(named, Has.Count.GreaterThan(10), "settings found in the prose");

        var unknown = named.Where(n => !accepted.Contains(n.Key) && !SettingsContract.IsOpenSection(n.Key))
            .Select(n => $"{n.File}: {n.Key}").Distinct().ToList();

        Assert.That(unknown, Is.Empty);
    }

    private static List<string> Codes(string text, string heading) =>
        Regex.Matches(text[text.IndexOf(heading, StringComparison.Ordinal)..], @"^\| `(\d)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Order().ToList();

    [Test]
    public void TheExitCodeTable_ListsTheCodesTheReferenceDoes()
    {
        var reference = Codes(File.ReadAllText(Path.Join(Root(), "docs", "end-user", "reference", "configuration.md")), "## Exit Codes");
        var plugin = Codes(File.ReadAllText(Path.Join(Plugin, "reference", "exit-codes.md")), "# Exit codes");

        Assert.That(reference, Has.Count.GreaterThanOrEqualTo(6));
        Assert.That(plugin, Is.EqualTo(reference));
    }

    // It runs and reads the tools, through skills only.
    [Test]
    public void ThePluginIsSkillsOnly()
    {
        foreach (var path in new[] { ".mcp.json", "hooks", "agents", "bin", ".lsp.json", "commands", "monitors" })
            Assert.That(File.Exists(Path.Join(Plugin, path)) || Directory.Exists(Path.Join(Plugin, path)), Is.False, path);
        var manifest = JObject.Parse(File.ReadAllText(Path.Join(Plugin, ".claude-plugin", "plugin.json")));
        foreach (var key in new[] { "mcpServers", "hooks", "agents", "commands", "lspServers", "experimental", "userConfig", "channels" })
            Assert.That(manifest.ContainsKey(key), Is.False, $"plugin.json declares {key}");
        Assert.That((string)manifest["name"], Is.EqualTo("schemasmith"));
    }

    private static readonly string[] ReadOnlySwitches = ["--Validate", "--WhatIf", "--Drift", "--TestConnection", "--PreviewTargets", "--version"];

    [Test]
    public void OnlyDeploy_ShowsASchemaQuenchCommandThatChangesADatabase()
    {
        var changing = Skills().Where(s => s != "deploy")
            .SelectMany(s => Regex.Matches(SkillText(s), @"`(SchemaQuench(?: --[^`]*)?)`").Select(m => (Skill: s, Command: m.Groups[1].Value)))
            .Where(c => !ReadOnlySwitches.Any(sw => c.Command.Contains(sw, StringComparison.OrdinalIgnoreCase)))
            .Select(c => $"{c.Skill}: {c.Command}").ToList();

        Assert.That(changing, Is.Empty);
        Assert.That(SkillText("deploy"), Does.Contain("`SchemaQuench --LogPath:"), "deploy shows the command it runs");
    }

    [Test]
    public void ThePluginsVersion_IsTheTools()
    {
        var props = XDocument.Load(Path.Join(Root(), "Directory.Build.props"));
        var version = props.Descendants("Version").First().Value;
        var manifest = JObject.Parse(File.ReadAllText(Path.Join(Plugin, ".claude-plugin", "plugin.json")));

        Assert.That((string)manifest["version"], Is.EqualTo(version));
    }

    // The words and two-word phrases the plugin's prose may not use, as SHA-256 hashes of their lower-case form, so the
    // test does not print them itself.
    private static readonly HashSet<string> OtherProductWords =
    [
        "a03407433890e7df44898f7a49665c320cf54af591e9b824a7ec90acf53c4212",
        "0e6cd7d000dec8a155903a2c5f5a6f46496e51d883e5528b05cae12f7dc4c578",
        "e48e5a4bf5cd28be6c146d461cecfa30c425d5b87c549578fe571f619913a225",
        "a33a54a73112eee8b33a5d97c267b922510cb98ac105420cd564f67e142009ee",
        "bcbe7ab642d1db0be5fa96318f456c9a1b086e1a74389634b9d167201e931473",
        "07ed400759a0f606a8b5bfa84712aabe7d1b1c45cb6536c8a5727446b6647b84",
        "bcdcf7f9e899672bd8572850b93691d590f9803e0b9865c7d9519c7c3182f2a1",
        "2b0a3d4f4cc41cb6fa4cfc8af42c57b3b7ef8805a3417cd8252da8e2616da09c",
        "88244594c37c3a027d5b905f291f99db80e2059b53067d432f5a51001408abb0",
    ];

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    // The public plugin names no other product.
    [Test]
    public void TheProseNamesNoOtherProduct()
    {
        var hits = AllProse().SelectMany(p =>
            {
                var words = Regex.Matches(p.Text.ToLowerInvariant(), @"[a-z0-9]+(?:-[a-z0-9]+)*").Select(m => m.Value).ToList();
                return words.Concat(words.Zip(words.Skip(1), (a, b) => $"{a} {b}"))
                    .Where(w => OtherProductWords.Contains(Sha256(w)))
                    .Select(w => $"{p.File}: \"{w}\"");
            })
            .Distinct().ToList();

        Assert.That(hits, Is.Empty);
    }
}
