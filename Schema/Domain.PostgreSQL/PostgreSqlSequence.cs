// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain.PostgreSQL
{
    /// <summary>
    /// A PostgreSQL sequence, declared rather than scripted.
    /// <para>Unlike an enum type, every attribute here is genuinely alterable in place
    /// (<c>ALTER SEQUENCE</c>), so this converges properly rather than having to refuse anything.</para>
    /// <para><b>The sequence's CURRENT VALUE is deliberately not a property.</b> It is data, not schema —
    /// it records how far the sequence has been consumed. A package that carried it would reset a live
    /// sequence's position on every deploy, handing out numbers that have already been used. <c>Start</c>
    /// is the declared starting point and only takes effect on creation or an explicit restart, which is
    /// a different thing entirely.</para>
    /// <para><b>Sequences the engine owns are never managed here.</b> A <c>serial</c> column or an
    /// <c>IDENTITY</c> column generates its own sequence; those belong to the column that created them and
    /// are excluded from extraction, the same way SQL Server's graph columns and temporal history tables
    /// are.</para>
    /// </summary>
    public class PostgreSqlSequence : DynamicBase
    {
        [SchemaProperty(Required = true, MaxLength = 63)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        /// <summary>smallint, integer or bigint. Determines the sequence's range.</summary>
        [SchemaProperty(Pattern = "smallint|integer|bigint",
            Description = "The sequence's data type: smallint, integer or bigint. Defaults to bigint, matching PostgreSQL.")]
        [JsonProperty(Order = 3)]
        [DefaultValue("bigint")]
        public string DataType { get; set; } = "bigint";

        [SchemaProperty(Description = "The value the sequence starts from when it is created or restarted. NOT the current value — that is data, and SchemaSmith never resets it.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public long? Start { get; set; }

        [SchemaProperty(Description = "Step between values. Negative for a descending sequence.")]
        [JsonProperty(Order = 5)]
        [DefaultValue(1L)]
        public long Increment { get; set; } = 1;

        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public long? MinValue { get; set; }

        [JsonProperty(Order = 7, NullValueHandling = NullValueHandling.Ignore)]
        public long? MaxValue { get; set; }

        [SchemaProperty(Minimum = 1, Description = "How many values are pre-allocated per session. Higher values are faster but leave larger gaps after a crash.")]
        [JsonProperty(Order = 8)]
        [DefaultValue(1L)]
        public long Cache { get; set; } = 1;

        [SchemaProperty(Description = "When true the sequence wraps to MinValue after reaching MaxValue instead of erroring.")]
        [JsonProperty(Order = 9)]
        public bool Cycle { get; set; }

        [JsonProperty(Order = 10, NullValueHandling = NullValueHandling.Ignore)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string VariantName { get; set; }
    }
}
