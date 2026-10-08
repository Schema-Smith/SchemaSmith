// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    public class Statistic : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [JsonProperty(Order = 2)]
        public string Kind { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 3)]
        public string StatisticsColumns { get; set; }

        [JsonProperty(Order = 4)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 5)]
        public string VariantName { get; set; }

        [SchemaProperty(Description = "The statistics object's schema, when it is not its table's. Left out, it is created in the table's schema.")]
        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }
    }
}
