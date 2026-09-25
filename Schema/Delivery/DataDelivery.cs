// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Domain;

namespace Schema.Delivery;

/// <summary>
/// Data delivery configuration for a table. Defines how table data is merged
/// during deployment.
/// </summary>
public class DataDelivery
{
    [SchemaProperty]
    [JsonProperty(Order = 1)]
    public string ContentFile { get; set; }

    // Product-side read measured: DataDeliveryProcessor compares OrdinalIgnoreCase, so "insert/update"
    // deploys and the pattern must accept it.
    [SchemaProperty(Pattern = "Insert|Insert/Update|Insert/Update/Delete", PatternIgnoreCase = true)]
    [JsonProperty(Order = 2)]
    public string MergeType { get; set; }

    // B1: the encoding of ContentFile — JSON (default, shredded with OPENJSON, requires SQL Server
    // compatibility level 130+) or XML (shredded with .nodes()/.value(), applies at every compat level).
    // The payload is user data in a shape SchemaSmith does not own, so this is an explicit author choice,
    // never inferred. Blank/absent = Json.
    [SchemaProperty(Pattern = "Json|Xml")]
    [JsonProperty(Order = 10)]
    public string ContentEncoding { get; set; }

    [SchemaProperty]
    [JsonProperty(Order = 3)]
    public string MatchColumns { get; set; }

    [SchemaProperty]
    [JsonProperty(Order = 4)]
    public string MergeFilter { get; set; }

    [SchemaProperty]
    [JsonProperty(Order = 5)]
    public bool MergeDisableTriggers { get; set; }

    /// <summary>PostgreSQL-specific: disable rules during merge.</summary>
    [SchemaProperty]
    [JsonProperty(Order = 6)]
    public bool MergeDisableRules { get; set; }

    /// <summary>PostgreSQL-specific: update descendant tables during merge.</summary>
    [SchemaProperty]
    [JsonProperty(Order = 7)]
    public bool MergeUpdateDescendents { get; set; }

    [SchemaProperty]
    [JsonProperty(Order = 8)]
    public string ShouldApplyExpression { get; set; }

    // Labels a conditional variant: the intent behind its ShouldApplyExpression. Appears in
    // delivery log lines whether the gate applies or skips; re-extraction preserves the whole
    // delivery array.
    [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional data-delivery variant — names the intent behind its ShouldApplyExpression, appears in deployment logging when the delivery applies, and identifies which array variant a DataTongs re-extraction reconciles.")]
    [JsonProperty(Order = 9)]
    public string VariantName { get; set; }
}
