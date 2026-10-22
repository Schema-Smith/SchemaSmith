// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    public class ExcludeConstraint : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [JsonProperty(Order = 2)]
        public string AccessMethod { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 3)]
        public List<ExcludeColumn> ExcludeColumns { get; set; } = [];

        [JsonProperty(Order = 4)]
        public string FilterExpression { get; set; }

        [JsonProperty(Order = 5)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 8)]
        public string VariantName { get; set; }

        [JsonProperty(Order = 6)]
        public bool Deferrable { get; set; }

        [JsonProperty(Order = 7)]
        public bool InitiallyDeferred { get; set; }

        // The constraint's backing index carries the same extras as a declared index (PostgreSqlIndex): covering
        // columns, storage parameters and placement. A key's operator class, COLLATE and parameters go in Column text.
        [SchemaProperty(Description = "Comma-separated covering columns (INCLUDE) on the constraint's backing index.")]
        [JsonProperty(Order = 9, NullValueHandling = NullValueHandling.Ignore)]
        public string IncludeColumns { get; set; }

        /// <summary>
        /// The backing index's <c>WITH (...)</c> storage parameters, <c>fillfactor</c> included: an exclusion constraint
        /// has no FillFactor property of its own, so the option is an ordinary entry here. A change replaces the
        /// constraint; comparison sorts by key because <c>reloptions</c> reorders itself.
        /// </summary>
        [JsonProperty(Order = 10, NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> StorageParameters { get; set; }

        [SchemaProperty(MaxLength = 128,
            Description = "PostgreSQL only. The tablespace of the constraint's backing index (USING INDEX TABLESPACE). Omit to leave placement unmanaged.")]
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string Tablespace { get; set; }

        public class ExcludeColumn
        {
            [SchemaProperty(Required = true)]
            [JsonProperty(Order = 1)]
            public string Column { get; set; }

            [SchemaProperty(Required = true)]
            [JsonProperty(Order = 2)]
            public string Operator { get; set; }
        }
    }
}
