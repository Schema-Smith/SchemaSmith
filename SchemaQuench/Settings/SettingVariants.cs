// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Schema.Domain.Settings;

namespace SchemaQuench.Settings;

/// <summary>Picks the declarations that apply to this server: an ungated one always, a gated one when its gate holds. Two
/// that both apply to one Kind and Name are an error, never "the last one wins".</summary>
public static class SettingVariants
{
    public static IReadOnlyList<Setting> Select(IReadOnlyList<Setting> declared, Func<string, bool> gate, out string error)
    {
        error = null;
        var active = declared.Where(s => string.IsNullOrWhiteSpace(s.ShouldApplyExpression) || gate(s.ShouldApplyExpression)).ToList();
        var clash = active.GroupBy(s => (Kind: s.Kind.ToLowerInvariant(), Name: s.Name.ToLowerInvariant())).FirstOrDefault(g => g.Count() > 1);
        if (clash == null) return active;

        var first = clash.First();
        error = $"{first.Display}: variants {string.Join(" and ", clash.Select(s => $"'{s.VariantName ?? "(no VariantName)"}'"))} both apply";
        return [];
    }
}
