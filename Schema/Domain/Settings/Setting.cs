// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Domain.Settings;

public enum SettingScope { Server, Database }

public enum DriftLevel { Warn, Error, Ignore }

public sealed class Setting
{
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }

    // Nullable so a declaration that leaves either out is refused instead of taking a default.
    public bool? Fix { get; set; }
    public DriftLevel? OnDrift { get; set; }

    public string ShouldApplyExpression { get; set; }
    public string VariantName { get; set; }

    public string Display => $"{Kind} '{Name}'";
}
