// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Schema.Domain.Settings;

public enum SettingScope { Server, Database }

public enum DriftLevel { Warn, Error, Ignore }

public sealed class Setting
{
    [SchemaProperty(Required = true)]
    public string Kind { get; set; }
    [SchemaProperty(Required = true)]
    public string Name { get; set; }
    [SchemaProperty(Required = true)]
    public string Value { get; set; }

    // Nullable so a declaration that leaves either out is refused instead of taking a default.
    [SchemaProperty(Required = true)]
    public bool? Fix { get; set; }
    [SchemaProperty(Required = true)]
    [JsonConverter(typeof(StringEnumConverter))]
    public DriftLevel? OnDrift { get; set; }

    public string ShouldApplyExpression { get; set; }
    public string VariantName { get; set; }

    [JsonIgnore]
    public string Display => $"{Kind} '{Name}'";
}
