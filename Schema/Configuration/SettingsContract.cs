// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Schema.Configuration;

/// <summary>Which tool a settings key belongs to.</summary>
public enum SettingsTool
{
    SchemaQuench,
    SchemaTongs,
    DataTongs,
    SchemaShears
}

/// <summary>
/// The enumerable shape of each tool's settings surface, and the runtime check that consumes it.
/// <para>A mistyped configuration key is silently inert: <c>Target:Sever</c> binds nothing and the run
/// proceeds as though the setting were absent. That is the same defect 2.4.0 fixed on the command line
/// with <c>CommandLineParser.WarnOnUnrecognizedArguments</c> — a switch that goes unread is worse than
/// one that errors — arriving through a different door, so it gets the same treatment: know the
/// accepted set, warn on anything outside it.</para>
/// </summary>
public static class SettingsContract
{
    /// <summary>
    /// Sections whose children are user-defined values rather than settings. Anything beneath one of
    /// these is data the operator chose to supply (a token name, a driver connection property, a
    /// per-template target override), so it can never be "unrecognised".
    /// </summary>
    private static readonly string[] OpenSections =
    [
        SettingsKeys.ScriptTokens,
        SettingsKeys.Target.ConnectionProperties,
        SettingsKeys.Source.ConnectionProperties,
        SettingsKeys.Target.TemplateTargets,
        SettingsKeys.FolderMapping,
        SettingsKeys.LogHygiene
    ];

    /// <summary>
    /// Keys bound positionally from a JSON array (<c>Target:Templates:0</c>, <c>:1</c>, …). The prefix
    /// is the setting; the numeric children are elements, not members.
    /// </summary>
    private static readonly string[] ArrayPrefixes =
    [
        SettingsKeys.Target.Templates,
        SettingsKeys.Target.Databases,
        SettingsKeys.Target.Schemas,
        SettingsKeys.TablesToExtract
    ];

    private static readonly Dictionary<SettingsTool, HashSet<string>> ToolKeys = new()
    {
        [SettingsTool.SchemaQuench] = new(BaseTargetKeys().Concat(
        [
            SettingsKeys.SchemaPackagePath, SettingsKeys.MaxThreads, SettingsKeys.WhatIfOnly,
            SettingsKeys.RunScriptsTwice, SettingsKeys.DropTablesRemovedFromProduct,
            SettingsKeys.DropUnknownIndexes, SettingsKeys.DropColumnsRemovedFromProduct,
            SettingsKeys.DropForeignKeysRemovedFromProduct, SettingsKeys.DropCheckConstraintsRemovedFromProduct,
            SettingsKeys.DropExcludeConstraintsRemovedFromProduct, SettingsKeys.DropStatisticsRemovedFromProduct,
            SettingsKeys.DropIndexesRemovedFromProduct, SettingsKeys.DropPeriodsRemovedFromProduct,
            SettingsKeys.DropEventsRemovedFromProduct, SettingsKeys.DropSchemaBoundDependents,
            SettingsKeys.RebuildPolicyMode, SettingsKeys.RebuildPolicyThreshold,
            SettingsKeys.RebuildPolicyOnOrderMismatch, SettingsKeys.SystemVersioningAlterHistory,
            SettingsKeys.PreventDrop, SettingsKeys.UpdateTables,
            SettingsKeys.KindleTheForge, SettingsKeys.ForceReKindle, SettingsKeys.CheckpointDirectory,
            SettingsKeys.DeliverData, SettingsKeys.VerboseLogging, SettingsKeys.FailureContextLines,
            SettingsKeys.BottleneckThresholdMs, SettingsKeys.TrackRunOnceMigrations,
            SettingsKeys.PruneObsoleteMigrationTracking,
            SettingsKeys.ArtifactPath, SettingsKeys.ScrubArtifacts, SettingsKeys.ScriptTokens
        ]), StringComparer.OrdinalIgnoreCase),

        [SettingsTool.SchemaTongs] = new(BaseSourceKeys().Concat(ShouldCastKeys(SettingsTool.SchemaTongs)).Concat(
        [
            SettingsKeys.ProductKeys.Name, SettingsKeys.ProductKeys.Path,
            SettingsKeys.ProductKeys.CheckConstraintStyle,
            SettingsKeys.ProductKeys.ObjectOrder, SettingsKeys.ProductKeys.PreserveExistingOrder,
            SettingsKeys.TemplateKeys.Name, SettingsKeys.TemplateKeys.SchemaIdentificationScript,
            SettingsKeys.OrphanHandling.Mode, SettingsKeys.FolderMapping, SettingsKeys.LogHygiene,
            SettingsKeys.ScriptTokens, SettingsKeys.TemplatePath
        ]), StringComparer.OrdinalIgnoreCase),

        [SettingsTool.DataTongs] = new(BaseSourceKeys().Concat(ShouldCastKeys(SettingsTool.DataTongs)).Concat(
        [
            SettingsKeys.ContentPath, SettingsKeys.ScriptPath, SettingsKeys.TemplatePath,
            SettingsKeys.TablesToExtract, SettingsKeys.ProductKeys.Name, SettingsKeys.TemplateKeys.Name,
            SettingsKeys.ScriptTokens
        ]), StringComparer.OrdinalIgnoreCase),

        [SettingsTool.SchemaShears] = new(
        [
            SettingsKeys.SourcePath, SettingsKeys.ManifestPath, SettingsKeys.AlwaysIncludePath,
            SettingsKeys.OutputPath, SettingsKeys.Zip, SettingsKeys.AllowDrops
        ], StringComparer.OrdinalIgnoreCase)
    };

    private static IEnumerable<string> BaseTargetKeys() =>
    [
        SettingsKeys.Target.Server, SettingsKeys.Target.User, SettingsKeys.Target.Password,
        SettingsKeys.Target.Port, SettingsKeys.Target.Platform, SettingsKeys.Target.IntegratedSecurity,
        SettingsKeys.Target.SecondaryServers, SettingsKeys.Target.ConnectionProperties,
        SettingsKeys.Target.Templates, SettingsKeys.Target.Databases, SettingsKeys.Target.Schemas,
        SettingsKeys.Target.TemplateTargets, SettingsKeys.UnsupportedFeaturePolicy,
        SettingsKeys.CompatEncoding
    ];

    private static IEnumerable<string> BaseSourceKeys() =>
    [
        SettingsKeys.Source.Server, SettingsKeys.Source.User, SettingsKeys.Source.Password,
        SettingsKeys.Source.Port, SettingsKeys.Source.Platform, SettingsKeys.Source.Database,
        SettingsKeys.Source.Schema, SettingsKeys.Source.IntegratedSecurity,
        SettingsKeys.Source.ConnectionProperties, SettingsKeys.SourceCompatEncoding
    ];

    // Only the keys the given tool actually reads. Handing both tools the whole set made the contract
    // over-claim: SchemaTongs accepted ShouldCast:MergeUpdate and DataTongs accepted ShouldCast:Collations,
    // neither of which those tools read, so a real key in the wrong settings file did nothing and said
    // nothing. The reader is recorded on the constant itself (see ReadByAttribute).
    private static IEnumerable<string> ShouldCastKeys(SettingsTool tool) =>
        typeof(SettingsKeys.ShouldCast).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(SettingsKeys.ShouldCast.Section))
            .Where(f => f.GetCustomAttribute<ReadByAttribute>()?.Tool == tool)
            .Select(f => (string)f.GetRawConstantValue());

    /// <summary>Every key the given tool accepts. Ordered for stable display.</summary>
    public static IReadOnlyCollection<string> AcceptedKeys(SettingsTool tool) =>
        ToolKeys[tool].OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every key any tool accepts — the union used by the drift-guard test.</summary>
    public static IReadOnlyCollection<string> AllAcceptedKeys() =>
        ToolKeys.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Sections whose children are operator-supplied values rather than setting names. Exposed so a
    /// consumer can reproduce the "is this a real setting?" decision without re-deriving the rule.
    /// </summary>
    public static IReadOnlyCollection<string> OpenSectionNames() => OpenSections.ToList();

    /// <summary>Settings bound positionally from a JSON array.</summary>
    public static IReadOnlyCollection<string> ArrayKeyNames() => ArrayPrefixes.ToList();

    /// <summary>
    /// The contract as JSON, for a consumer that cannot reference this assembly — and as the source of
    /// the committed <c>settings-contract.json</c>, which a test pins to this output so the published
    /// artifact can never describe a surface the code no longer has.
    /// </summary>
    public static string ToJson()
    {
        var doc = new SettingsContractDocument
        {
            Description = "Machine-readable projection of the SchemaSmith settings contract. " +
                          "Generated from Schema.Configuration.SettingsContract — do not hand-edit; " +
                          "a unit test fails if this file and the code disagree.",
            Tools = Enum.GetValues<SettingsTool>()
                .ToDictionary(t => t.ToString(), t => AcceptedKeys(t).ToArray()),
            Open = OpenSectionNames().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            Arrays = ArrayKeyNames().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed class SettingsContractDocument
    {
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("tools")] public Dictionary<string, string[]> Tools { get; set; }
        [JsonPropertyName("openSections")] public string[] Open { get; set; }
        [JsonPropertyName("arrayKeys")] public string[] Arrays { get; set; }
    }

    public static bool IsOpenSection(string key) =>
        OpenSections.Any(s => key.StartsWith(s + ":", StringComparison.OrdinalIgnoreCase) ||
                              key.Equals(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Keys the given tool does not recognise, restricted to sections it demonstrably owns.
    /// <para>Scoping to owned sections is what makes this usable rather than noisy. A settings file may
    /// carry sections belonging to another tool, a test harness, or a future version; reporting those
    /// would bury the one case that matters — a mistyped member of a section the run is clearly using,
    /// like <c>Target:Sever</c>. Unknown top-level sections are therefore left alone.</para>
    /// </summary>
    public static IReadOnlyList<string> UnrecognizedKeys(IConfiguration config, SettingsTool tool)
    {
        if (config == null) return [];

        var accepted = ToolKeys[tool];
        var ownedSections = accepted
            .Where(k => k.Contains(':'))
            .Select(k => k[..k.IndexOf(':')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Flatten(config)
            .Where(k => k.Contains(':'))
            .Where(k => ownedSections.Contains(k[..k.IndexOf(':')]))
            .Where(k => !accepted.Contains(k))
            .Where(k => !IsOpenSection(k))
            .Where(k => !IsArrayElement(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Logs a warning naming each unrecognised key. Warns rather than fails, matching
    /// <c>CommandLineParser.WarnOnUnrecognizedArguments</c>: the key may belong to a newer version or a
    /// deliberately shared file, so the operator decides.
    /// </summary>
    public static void WarnOnUnrecognizedKeys(IConfiguration config, SettingsTool tool, Action<string> warn)
    {
        if (warn == null) return;
        foreach (var key in UnrecognizedKeys(config, tool))
            warn($"Configuration key '{key}' is not read by {tool} and will have no effect. Check for a typo.");
    }

    // An array element is a numeric child of an array-valued setting (Target:Databases:0).
    private static bool IsArrayElement(string key)
    {
        var lastSeparator = key.LastIndexOf(':');
        if (lastSeparator < 0 || !int.TryParse(key[(lastSeparator + 1)..], out _)) return false;
        var prefix = key[..lastSeparator];
        return ArrayPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase);
    }

    // Leaf keys only: a section that merely holds children is not itself a supplied value.
    private static IEnumerable<string> Flatten(IConfiguration config)
    {
        foreach (var child in config.GetChildren())
        {
            var grandChildren = child.GetChildren().ToList();
            if (grandChildren.Count == 0)
                yield return child.Path;
            else
                foreach (var nested in Flatten(child))
                    yield return nested;
        }
    }
}
