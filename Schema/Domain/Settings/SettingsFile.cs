// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Domain.Settings;

/// <summary><c>ServerSettings.json</c> (beside Product.json) or <c>DatabaseSettings.json</c> (in a template folder). Absent
/// means unmanaged: <see cref="LoadIfPresent"/> returns null and nothing reads or changes a setting.</summary>
public sealed class SettingsFile
{
    public const string ServerFileName = "ServerSettings.json";
    public const string DatabaseFileName = "DatabaseSettings.json";

    // Lets an editor-facing "$schema" key load under MissingMemberHandling.Error, as on Product.json.
    [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
    public string SchemaRef { get; set; }

    public List<Setting> Settings { get; set; } = [];

    [JsonIgnore] public string FilePath { get; set; }
    [JsonIgnore] public SettingScope Scope { get; set; }

    public static SettingsFile LoadIfPresent(string path, Platform platform, SettingScope scope,
        MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error, bool tolerateDeclarationErrors = false)
    {
        var files = ProductFileWrapper.GetFromFactory();
        if (!files.Exists(path)) return null;

        var settings = JsonText.Settings(new JsonSerializerSettings { MissingMemberHandling = missingMemberHandling });
        var file = JsonHelper.WithFileContext(path, () => JsonConvert.DeserializeObject<SettingsFile>(files.ReadAllText(path), settings))
                   ?? new SettingsFile();
        file.Settings ??= [];
        file.FilePath = path;
        file.Scope = scope;
        if (!tolerateDeclarationErrors) file.RefuseInvalidDeclarations(platform);
        return file;
    }

    private void RefuseInvalidDeclarations(Platform platform)
    {
        var kinds = SettingKinds.For(platform, Scope);
        foreach (var setting in Settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Name))
                throw new PackageException($"A {setting.Kind} setting in {FilePath} has no Name.");
            if (!kinds.Contains(setting.Kind ?? "", StringComparer.OrdinalIgnoreCase))
                throw new PackageException($"Setting {setting.Display} in {FilePath} has Kind '{setting.Kind}', which is not a " +
                                           $"{Scope.ToString().ToLowerInvariant()} setting kind on {platform.ToDisplayName()}: use {string.Join(", ", kinds)}.");
            if (setting.Value == null)
                throw new PackageException($"Setting {setting.Display} in {FilePath} must state a Value; an empty one is written \"\".");
            if (setting.Fix == null || setting.OnDrift == null)
                throw new PackageException($"Setting {setting.Display} in {FilePath} must state Fix and OnDrift: " +
                                           "Fix true or false, OnDrift Warn, Error or Ignore.");
        }
    }
}
