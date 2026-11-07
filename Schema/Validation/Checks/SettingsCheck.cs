// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Domain;
using Schema.Domain.Settings;

namespace Schema.Validation.Checks;

/// <summary>
/// The declarations in ServerSettings.json and DatabaseSettings.json that a deploy would refuse: a Kind the platform does
/// not have (<c>SS-SET-001</c>), a setting with no Name or that leaves out Fix or OnDrift (<c>SS-SET-002</c>), one setting
/// declared twice where either declaration is ungated (<c>SS-SET-003</c>), and database settings on a schema template
/// (<c>SS-SET-004</c>). Setting names are not checked: which names exist depends on the target's version.
/// </summary>
public sealed class SettingsCheck : ISchemaCheck
{
    public const string UnknownKindCode = "SS-SET-001";
    public const string IncompleteCode = "SS-SET-002";
    public const string DuplicateCode = "SS-SET-003";
    public const string SchemaTemplateCode = "SS-SET-004";
    private const string Category = "Settings";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        foreach (var f in Check(ctx.Platform, ctx.Product.ServerSettings, "")) yield return f;
        foreach (var template in ctx.Templates)
        {
            var file = template.DatabaseSettings;
            if (file == null) continue;
            var prefix = $"Template '{template.Name}' / ";
            if (template.IsSchemaTemplate)
                yield return new Finding(Severity.Error, SchemaTemplateCode, Category, prefix + SettingsFile.DatabaseFileName,
                    $"Template '{template.Name}' is a schema template, and database settings cannot fan out per schema: a setting " +
                    "runs once per database. Move the file to a regular template that targets the same databases.");
            foreach (var f in Check(ctx.Platform, file, prefix)) yield return f;
        }
    }

    private static IEnumerable<Finding> Check(Platform platform, SettingsFile file, string prefix)
    {
        if (file == null) yield break;
        var where = prefix + Path.GetFileName(file.FilePath);
        var kinds = SettingKinds.For(platform, file.Scope);
        foreach (var s in file.Settings)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                yield return new Finding(Severity.Error, IncompleteCode, Category, where, $"A {s.Kind} setting has no Name.");
                continue;
            }
            if (!kinds.Contains(s.Kind ?? "", StringComparer.OrdinalIgnoreCase))
                yield return new Finding(Severity.Error, UnknownKindCode, Category, $"{where} / {s.Display}",
                    $"Kind '{s.Kind}' is not a {file.Scope.ToString().ToLowerInvariant()} setting kind on {platform.ToDisplayName()}: " +
                    $"use {string.Join(", ", kinds)}.");
            if (s.Fix == null || s.OnDrift == null)
                yield return new Finding(Severity.Error, IncompleteCode, Category, $"{where} / {s.Display}",
                    "A setting must state Fix and OnDrift: Fix true or false, OnDrift Warn, Error or Ignore. SchemaTongs writes both.");
        }

        var duplicates = file.Settings.Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => (Kind: s.Kind?.ToLowerInvariant(), Name: s.Name.ToLowerInvariant()))
            .Where(g => g.Count() > 1 && g.Any(s => string.IsNullOrWhiteSpace(s.ShouldApplyExpression)));
        foreach (var g in duplicates)
            yield return new Finding(Severity.Error, DuplicateCode, Category, $"{where} / {g.First().Display}",
                $"{g.First().Display} is declared {g.Count()} times and at least one declaration has no ShouldApplyExpression, " +
                "so more than one could apply. Give each variant a ShouldApplyExpression, or keep one declaration.");
    }
}
