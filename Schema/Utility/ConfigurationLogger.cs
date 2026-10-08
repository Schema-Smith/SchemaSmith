// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;

namespace Schema.Utility;

public static class ConfigurationLogger
{
    public static void LogConfiguration(IConfigurationRoot config, Action<string> logLine)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        logLine?.Invoke($"Version: {version}");

        var entries = config
            .GetChildren().Where(s => !s.Key.EqualsIgnoringCase("Description"))
            .SelectMany(s => config.GetSection(s.Key).AsEnumerable())
            .OrderBy(s => PadArrayIndexInKey(s.Key)) // preserve the actual order of array items
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        logLine?.Invoke("Configuration:");
        var hygiene = LogHygieneOptions.FromConfiguration(config);
        var arrayNameKeys = new HashSet<string>();
        foreach (var entry in entries)
        {
            var key = entry.Key;
            if (arrayNameKeys.Contains(entry.Key)) continue; // skip array name keys we've already logged
            var indents = 1;
            while (key.Contains(':'))
            {
                indents++;
                key = TryIndexToItemName(key.Substring(key.IndexOf(":", StringComparison.Ordinal) + 1), entries, entry, arrayNameKeys);
            }
            var value = ShouldMask(entry.Key, key, hygiene)
                ? LogScrubber.Mask
                : LogScrubber.ScrubConnectionStringSubfields(entry.Value ?? ""); // strip any embedded connection-string password
            logLine?.Invoke($"{new string(' ', indents * 2)}{key}: {value}");
        }

        logLine?.Invoke("");
        logLine?.Invoke("");
    }

    public static void LogCommandLine(IConfigurationRoot config, Action<string> logLine)
    {
        var hygiene = LogHygieneOptions.FromConfiguration(config);
        logLine?.Invoke("Command line:");

        var switches = CommandLineParser.SwitchesAndValues;
        if (switches.Count == 0)
        {
            logLine?.Invoke("  (none)");
        }
        else
        {
            foreach (var entry in switches)
            {
                var value = LogScrubber.ScrubTokenValue(entry.Key, entry.Value ?? "", hygiene);
                logLine?.Invoke($"  {entry.Key}: {value}");
            }
        }

        logLine?.Invoke("");
        logLine?.Invoke("");
    }

    // A value is masked when its own name is sensitive, when it is an entry of a list whose name is sensitive (the
    // entry's own name is just its index, "password:0"), and, with LogTokens off, when it is a script token. Objects
    // are still judged leaf by leaf: masking everything under a sensitive ancestor would hide every ScriptTokens
    // value, since "Token" is one of the sensitive patterns.
    private static bool ShouldMask(string fullKey, string displayName, LogHygieneOptions hygiene)
    {
        if (LogScrubber.ShouldScrubName(displayName, hygiene)) return true;

        var segments = fullKey.Split(':');
        if (!hygiene.LogTokens && segments.Length > 1 && segments[0].EqualsIgnoringCase(SettingsKeys.ScriptTokens))
            return true;

        var owner = segments.Length - 1;
        while (owner >= 0 && int.TryParse(segments[owner], out _)) owner--;
        return owner < segments.Length - 1 && owner >= 0 && LogScrubber.ShouldScrubName(segments[owner], hygiene);
    }

    private static string TryIndexToItemName(string key, Dictionary<string, string> entries, KeyValuePair<string, string> entry, HashSet<string> arrayNameKeys)
    {
        if (int.TryParse(key, out _))
        {
            // if the key is an int and has a name subkey, we assume it's an array index and use the name instead
            if (entries.ContainsKey($"{entry.Key}:Name"))
            {
                key = entries[$"{entry.Key}:Name"];
                arrayNameKeys.Add($"{entry.Key}:Name");
            }
        }

        return key;
    }

    private static string PadArrayIndexInKey(string key)
    {
        const string pattern = @"(?<=:)\d+(?=:)";
        return Regex.Replace($"{key}:", pattern, match => $"{(int.TryParse(match.Value, out var number) ? number.ToString("D5") : match.Value)}");
    }
}
