// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain.SqlServer
{
    public class FullTextIndex : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string FullTextCatalog { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public string KeyIndex { get; set; }

        [JsonProperty(Order = 3)]
        [DefaultValue("AUTO")]
        public string ChangeTracking { get; set; } = "AUTO";

        [JsonProperty(Order = 4)]
        [DefaultValue("SYSTEM")]
        public string StopList { get; set; } = "SYSTEM";

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 5)]
        public string Columns { get; set; }

        [JsonProperty(Order = 6)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 7)]
        public string VariantName { get; set; }
    }
}
