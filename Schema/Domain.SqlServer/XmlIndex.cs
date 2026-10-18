// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    public class XmlIndex : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [JsonProperty(Order = 2)]
        public bool IsPrimary { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 3)]
        public string Column { get; set; }

        [JsonProperty(Order = 4)]
        public string PrimaryIndex { get; set; }

        [SchemaProperty(Pattern = "VALUE|PATH|PROPERTY")]
        [JsonProperty(Order = 5)]
        public string SecondaryIndexType { get; set; }

        // Each XML index, primary or secondary, has its own setting; a secondary does not inherit its primary's.
        [SchemaProperty(Description = "SQL Server 2022+. Compresses this XML index (XML_COMPRESSION). A secondary XML index has its own setting and does not inherit its primary's. Extracted where the server reports sys.partitions.xml_compression (2025, and 2022 from CU26); elsewhere SchemaTongs carries the value forward from the package it is refreshing.")]
        [JsonProperty(Order = 8)]
        public bool XmlCompression { get; set; }

        [JsonProperty(Order = 6)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 7)]
        public string VariantName { get; set; }
    }
}
