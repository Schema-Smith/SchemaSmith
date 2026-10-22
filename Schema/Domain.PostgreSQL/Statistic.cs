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

        [SchemaProperty(Minimum = 0, Maximum = 10000,
            Description = "PostgreSQL 13+. The statistics target (ALTER STATISTICS … SET STATISTICS n), the sample size ANALYZE uses for this object. Omit to leave it unmanaged; declared, it is applied when the object is created and converged in place when it changes. Below 13 a declared target follows the unsupported-feature policy.")]
        [JsonProperty(Order = 7, NullValueHandling = NullValueHandling.Ignore)]
        public int? StatisticsTarget { get; set; }
    }
}
