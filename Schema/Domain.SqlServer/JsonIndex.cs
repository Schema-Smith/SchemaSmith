// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    /// <summary>
    /// A JSON index (<c>CREATE JSON INDEX</c>, SQL Server 2025) on a <c>json</c> column. Its key is one column and a set of
    /// SQL/JSON paths, not a column list, so it is its own list on the table, as <see cref="XmlIndex"/> is. The paths are a
    /// set compared as written; none means the whole document (<c>$</c>). A declared attribute that differs re-creates
    /// the index; <see cref="FillFactor"/> is compared only when declared.
    /// </summary>
    public class JsonIndex : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [SchemaProperty(Required = true, Description = "The json column the index covers. The table needs a clustered primary key, and a column takes one JSON index.")]
        [JsonProperty(Order = 2)]
        public string Column { get; set; }

        [SchemaProperty(Description = "The FOR list: the SQL/JSON paths to index, such as $.customer.id. No two may overlap, and wildcards are not allowed. Omitted or empty, the whole document ($) is indexed.")]
        [JsonProperty(Order = 3)]
        public List<string> Paths { get; set; } = [];

        public bool ShouldSerializePaths() => Paths is { Count: > 0 };

        [SchemaProperty(Description = "OPTIMIZE_FOR_ARRAY_SEARCH: also index array elements, for JSON_CONTAINS searches. Omitted, off.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public bool? OptimizeForArraySearch { get; set; }

        [SchemaProperty(Minimum = 1, Maximum = 100, Description = "FILLFACTOR. Omitted, the server's default, and not compared.")]
        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public byte? FillFactor { get; set; }

        [JsonProperty(Order = 6)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 7)]
        public string VariantName { get; set; }
    }
}
