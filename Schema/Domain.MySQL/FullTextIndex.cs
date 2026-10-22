// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain.MySQL
{
    public class FullTextIndex : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public string Columns { get; set; }

        [JsonProperty(Order = 3)]
        public string Parser { get; set; }

        [SchemaProperty(Description = "Optional. The index's COMMENT. An omitted value clears a comment the server has, as the engine's own DDL does.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        // TRANSITIONAL (Comment is the pre-2.8 spelling of Description on MySQL and MariaDB; roadmap WS-13 tracks its removal)
        [SchemaProperty(Deprecated = true, Description = "Deprecated alias of Description. Still honoured on deploy; --Validate reports it (SS-DESC-002).")]
        [JsonProperty(Order = 4)]
        public string Comment { get; set; }

        [JsonProperty(Order = 20)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 21)]
        public string VariantName { get; set; }
    }
}
