// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Schema.Domain.Settings;
using Schema.Settings;

namespace SchemaTongs;

public enum SettingsPolicyDefault { Report, EnforceWarn, EnforceError }

public enum SettingsOutput { Json, Script, Both }

/// <summary>
/// Which of a server's settings extraction writes, and as what. Without an allowlist: those that differ from the engine's
/// default and do not name this machine. With one: exactly the listed ones, default or not. Every written setting states
/// its policy, so a package never relies on one being assumed.
/// </summary>
public static class SettingsCaster
{
    public static (SettingsFile File, IReadOnlyList<string> NotOnServer) Capture(IEnumerable<CapturedSetting> all, SettingScope scope,
        IReadOnlyCollection<(string Kind, string Name)> allowlist, SettingsPolicyDefault policy)
    {
        var captured = all.ToList();
        var notOnServer = new List<string>();
        List<CapturedSetting> chosen;
        if (allowlist == null)
            chosen = captured.Where(s => !s.IsDefault && !s.IsMachineIdentity).ToList();
        else
        {
            var wanted = allowlist.Select(Key).ToHashSet();
            chosen = captured.Where(s => wanted.Contains(Key((s.Kind, s.Name)))).ToList();
            var found = chosen.Select(s => Key((s.Kind, s.Name))).ToHashSet();
            notOnServer.AddRange(allowlist.Where(a => !found.Contains(Key(a))).Select(a => $"{a.Kind} '{a.Name}'"));
        }

        var (fix, onDrift) = policy switch
        {
            SettingsPolicyDefault.EnforceWarn => (true, DriftLevel.Warn),
            SettingsPolicyDefault.EnforceError => (true, DriftLevel.Error),
            _ => (false, DriftLevel.Warn)
        };
        var settings = chosen
            .OrderBy(s => s.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new Setting { Kind = s.Kind, Name = s.Name, Value = s.Value, Fix = fix, OnDrift = onDrift })
            .ToList();
        return (new SettingsFile { Scope = scope, Settings = settings }, notOnServer);
    }

    public static IReadOnlyList<(string FileName, string Sql)> Scripts(SettingsFile file, Func<string, ISettingDescriptor> descriptorFor, string database) =>
        file.Settings.Select(s => (ScriptFileName(s), descriptorFor(s.Kind).RenderScript(s.Name, s.Value, database))).ToList();

    public static string ScriptFileName(Setting s)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string($"{s.Kind}.{s.Name}".Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return name + ".sql";
    }

    private static string Key((string Kind, string Name) s) => $"{s.Kind?.ToLowerInvariant()}|{s.Name?.ToLowerInvariant()}";
}

/// <summary>The <c>ShouldCast:SettingsAllowlist</c> file: <c>{ "Server": [ { "Kind", "Name" } ], "Database": [ ... ] }</c>.</summary>
public sealed class SettingsAllowlist
{
    private readonly Dictionary<SettingScope, List<(string Kind, string Name)>> _byScope = new();

    public IReadOnlyList<(string Kind, string Name)> For(SettingScope scope) => _byScope.TryGetValue(scope, out var list) ? list : [];

    public static SettingsAllowlist Load(string path)
    {
        if (!File.Exists(path)) throw new Schema.Utility.RunFailedException($"ShouldCast:SettingsAllowlist names '{path}', which does not exist.");
        var root = JObject.Parse(File.ReadAllText(path));
        var allowlist = new SettingsAllowlist();
        foreach (var scope in Enum.GetValues<SettingScope>())
        {
            if (root[scope.ToString()] is not JArray entries) continue;
            allowlist._byScope[scope] = entries.Select(e => ((string)e["Kind"], (string)e["Name"]))
                .Where(e => !string.IsNullOrWhiteSpace(e.Item1) && !string.IsNullOrWhiteSpace(e.Item2)).ToList();
        }
        return allowlist;
    }
}
