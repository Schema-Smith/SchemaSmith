// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;

namespace Schema.Utility;

/// <summary>
/// Per-tool log-hygiene configuration bound from the <c>LogHygiene</c> settings block. With no block
/// present the defaults apply: token logging on, only the baked-in <see cref="LogScrubber"/> patterns
/// scrubbed.
/// </summary>
public sealed class LogHygieneOptions
{
    public bool LogTokens { get; init; } = true;
    public HashSet<string> ScrubTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ScrubPatterns { get; } = [];
    public HashSet<string> AllowTokens { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static LogHygieneOptions Default { get; } = new();

    public static LogHygieneOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection(SettingsKeys.LogHygiene);
        var options = new LogHygieneOptions { LogTokens = section["LogTokens"]?.ToLower() != "false" };
        foreach (var t in ReadArray(section, "ScrubTokens")) options.ScrubTokens.Add(t);
        options.ScrubPatterns.AddRange(ReadArray(section, "ScrubPatterns"));
        foreach (var t in ReadArray(section, "AllowTokens")) options.AllowTokens.Add(t);
        return options;
    }

    private static readonly string[] ListKeys = ["ScrubTokens", "ScrubPatterns", "AllowTokens"];

    /// <summary>
    /// What in a <c>LogHygiene</c> block will not be read, each naming the key. Every one of these used to be ignored
    /// in silence, so a user who wrote <c>"ScrubTokens": "DeployKey"</c> believed a value was masked that was logged
    /// in clear.
    /// </summary>
    public static IEnumerable<string> Problems(IConfiguration config)
    {
        var section = config?.GetSection(SettingsKeys.LogHygiene);
        if (section == null || !section.Exists()) yield break;

        foreach (var child in section.GetChildren())
        {
            if (child.Key.Equals("LogTokens", StringComparison.OrdinalIgnoreCase))
            {
                if (!bool.TryParse(child.Value, out _))
                    yield return $"LogHygiene:LogTokens is '{child.Value ?? "(an object)"}', not true or false. Token values will be logged.";
            }
            else if (ListKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (child.Value != null)
                    yield return $"LogHygiene:{child.Key} is a single value, not a list, and is ignored. Write it as [ \"{child.Value}\" ].";
            }
            else
            {
                yield return $"LogHygiene:{child.Key} is not a setting and is ignored. The settings are LogTokens, ScrubTokens, ScrubPatterns and AllowTokens.";
            }
        }
    }

    private static IEnumerable<string> ReadArray(IConfiguration section, string key) =>
        section.GetSection(key).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());
}
