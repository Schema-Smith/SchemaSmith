// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Delivery;

/// <summary>
/// Post-extraction hook for configuring data delivery settings on table JSON files.
/// Called by DataTongs after extracting table data when ConfigureDataDelivery is enabled.
/// Writes ContentFile, MergeType, MatchColumns, MergeFilter, and trigger/rule settings
/// back into the table JSON.
/// </summary>
public interface IDataDeliveryConfigurator
{
    /// <summary>
    /// Configure data delivery settings for a single table.
    /// </summary>
    /// <param name="context">All inputs needed to locate and update the table JSON.</param>
    /// <returns>
    /// True when delivery was actually configured for this table (Table.json found and reconciled,
    /// whether changed or already up to date); false when it declined (no Table.json found, or an
    /// authored DataDelivery array had no matching/unambiguous VariantName). Callers use this to key
    /// merge-script suppression to delivery having actually been configured, per table.
    /// </returns>
    bool Configure(DataDeliveryConfiguratorContext context);
}

/// <summary>
/// Input context for a single table's data delivery configuration.
/// All paths and values are pre-resolved by the caller (DataTongs).
/// </summary>
public class DataDeliveryConfiguratorContext
{
    /// <summary>Template root directory path.</summary>
    public string TemplateRootPath { get; set; }

    /// <summary>Platform identifier (SqlServer, PostgreSQL, MySQL).</summary>
    public string Platform { get; set; }

    /// <summary>Schema/database name for the table.</summary>
    public string TableSchema { get; set; }

    /// <summary>Table name.</summary>
    public string TableName { get; set; }

    /// <summary>Path to the extracted content file (absolute).</summary>
    public string ContentFilePath { get; set; }

    /// <summary>Comma-separated key column names from the database.</summary>
    public string KeyColumns { get; set; }

    /// <summary>Default merge type if the table config doesn't specify one.</summary>
    public string DefaultMergeType { get; set; }

    /// <summary>Whether to disable triggers during merge.</summary>
    public bool DisableTriggers { get; set; }

    /// <summary>PostgreSQL-specific: whether to disable rules during merge.</summary>
    public bool DisableRules { get; set; }

    /// <summary>PostgreSQL-specific: whether to update descendant tables.</summary>
    public bool UpdateDescendents { get; set; }

    /// <summary>Per-table config overrides (MergeType, KeyColumns, Filter).</summary>
    public string MergeTypeOverride { get; set; }

    /// <summary>Per-table key column override.</summary>
    public string KeyColumnsOverride { get; set; }

    /// <summary>Per-table merge filter override.</summary>
    public string MergeFilterOverride { get; set; }

    /// <summary>
    /// Content-file encoding to stamp on the reconciled DataDelivery entry: "Xml" writes
    /// <c>"ContentEncoding": "Xml"</c>; "Json" (or blank) removes the property (JSON is the default).
    /// Set by DataTongs when extracting in the XML delivery encoding (B1 slice 3).
    /// </summary>
    public string ContentEncoding { get; set; }
    public string TimeZone { get; set; }

    /// <summary>
    /// Optional identity of the authored DataDelivery array variant this extraction targets.
    /// When the table's DataDelivery is an array, the configurator reconciles the single matching
    /// element (by VariantName) and leaves the rest untouched; blank means "single-object table or
    /// no target variant" and preserves the leave-array-untouched behavior.
    /// </summary>
    public string VariantName { get; set; }

    /// <summary>Callback for logging progress messages.</summary>
    public System.Action<string> ProgressLog { get; set; }

    /// <summary>Callback for logging warning messages.</summary>
    public System.Action<string> WarningLog { get; set; }
}
