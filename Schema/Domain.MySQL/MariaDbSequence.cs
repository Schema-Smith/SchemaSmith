// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.MySQL
{
    /// <summary>
    /// A declared MariaDB sequence (MariaDB 10.3+). Converged attribute by attribute with ALTER SEQUENCE;
    /// the current position is data and is never read or reset. MySQL has no sequence at any version.
    /// </summary>
    public class MariaDbSequence : DynamicBase
    {
        [SchemaProperty(Description = "Optional. Relative path to the generated JSON Schema, for editor validation and autocomplete. Ignored at deploy time.")]
        [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaRef { get; set; }

        [SchemaProperty(Required = true, MaxLength = 64)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [SchemaProperty(Pattern = "(tinyint|smallint|mediumint|int|integer|bigint)( unsigned)?",
            Description = "The sequence's integer type (MariaDB 11.5+): tinyint, smallint, mediumint, int or bigint, optionally unsigned. Defaults to bigint. Below 11.5 every sequence is a signed bigint and another type is a downgrade.")]
        [JsonProperty(Order = 2)]
        [DefaultValue("bigint")]
        public string DataType { get; set; } = "bigint";

        [SchemaProperty(Description = "The value the sequence starts from when it is created. NOT the current value — that is data, and SchemaSmith never resets it. Left out, MariaDB starts at MinValue (MaxValue for a descending sequence).")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public long? Start { get; set; }

        [SchemaProperty(Description = "Step between values. Negative for a descending sequence.")]
        [JsonProperty(Order = 4)]
        [DefaultValue(1L)]
        public long Increment { get; set; } = 1;

        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public long? MinValue { get; set; }

        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public long? MaxValue { get; set; }

        [SchemaProperty(Minimum = 0, Description = "How many values are pre-allocated per session. Defaults to 1000, matching MariaDB; 0 turns caching off.")]
        [JsonProperty(Order = 7)]
        [DefaultValue(1000L)]
        public long Cache { get; set; } = 1000;

        [SchemaProperty(Description = "When true the sequence wraps to MinValue after reaching MaxValue instead of erroring.")]
        [JsonProperty(Order = 8)]
        public bool Cycle { get; set; }

        [JsonProperty(Order = 9, NullValueHandling = NullValueHandling.Ignore)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deploy log messages when the variant applies.")]
        [JsonProperty(Order = 10, NullValueHandling = NullValueHandling.Ignore)]
        public string VariantName { get; set; }
    }
}
