// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Schema.Domain;

namespace Schema.Domain.PostgreSQL
{
    public class PostgreSqlMaterializedView : DynamicBase
    {
        // See Product.SchemaRef. Root of its own file, so it carries the reference; the nested
        // types on DynamicBase deliberately do not.
        [SchemaProperty(Description = "Optional. Relative path to the generated JSON Schema, for editor validation and autocomplete. Ignored at deploy time.")]
        [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaRef { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        // Default resolution moved to SchemaDefaultResolver. Regular templates: defaults to "public".
        // Schema templates: defaults to "{{SchemaName}}"; explicit literals are rejected at load.
        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 3)]
        public string Definition { get; set; } = "";

        [JsonProperty(Order = 4)]
        [DefaultValue(true)]
        public bool WithData { get; set; } = true;

        [JsonProperty(Order = 5)]
        public string Tablespace { get; set; }

        [JsonProperty(Order = 6)]
        public string AccessMethod { get; set; }

        [JsonProperty(Order = 7)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 8)]
        public string VariantName { get; set; }

        [JsonProperty(Order = 10)]
        public List<PostgreSqlIndex> Indexes { get; set; } = [];
    }
}
