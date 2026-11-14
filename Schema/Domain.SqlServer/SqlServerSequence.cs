// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using System.Numerics;
using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    /// <summary>
    /// A SQL Server sequence (2012+), declared rather than scripted, and converged in place with ALTER SEQUENCE.
    /// <para>Two attributes SQL Server cannot alter: the data type (ALTER SEQUENCE has no AS) is refused when it
    /// changes, and <c>Start</c> (no START WITH on ALTER) applies only when the sequence is created. The current value
    /// is data and is never touched -- no RESTART, ever.</para>
    /// <para>The start and bounds are <see cref="BigInteger"/>: a decimal(38,0) sequence's limits are 38 digits, beyond
    /// both <c>long</c> and .NET <c>decimal</c>. The increment fits a <c>long</c>.</para>
    /// </summary>
    public class SqlServerSequence : DynamicBase
    {
        // See Product.SchemaRef. Root of its own file, so it carries the reference.
        [SchemaProperty(Description = "Optional. Relative path to the generated JSON Schema, for editor validation and autocomplete. Ignored at deploy time.")]
        [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaRef { get; set; }

        [SchemaProperty(Required = true, MaxLength = 128)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [SchemaProperty(Description = "Optional. The sequence's schema; dbo when omitted, or the template's schema in a schema template.")]
        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        [SchemaProperty(Description = "tinyint, smallint, int, bigint, or decimal(p[,0]) / numeric(p[,0]) with p up to 38. A user-defined alias type is accepted and compared as its base type, which is what SQL Server records. Defaults to bigint. SQL Server cannot alter a sequence's type, so a changed type is refused.")]
        [JsonProperty(Order = 3)]
        [DefaultValue("bigint")]
        public string DataType { get; set; } = "bigint";

        [SchemaProperty(Description = "The first value, applied when the sequence is created. SQL Server cannot change it afterwards. NOT the current value -- that is data, and SchemaSmith never resets it.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public BigInteger? Start { get; set; }

        [SchemaProperty(Description = "Step between values. Negative for a descending sequence.")]
        [JsonProperty(Order = 5)]
        [DefaultValue(1L)]
        public long Increment { get; set; } = 1;

        [SchemaProperty(Description = "Optional. The lowest value. Omitted means the data type's minimum.")]
        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public BigInteger? MinValue { get; set; }

        [SchemaProperty(Description = "Optional. The highest value. Omitted means the data type's maximum.")]
        [JsonProperty(Order = 7, NullValueHandling = NullValueHandling.Ignore)]
        public BigInteger? MaxValue { get; set; }

        [SchemaProperty(Minimum = 0, Description = "Optional. Values pre-allocated in memory. Omitted means SQL Server's default cache; 0 means NO CACHE.")]
        [JsonProperty(Order = 8, NullValueHandling = NullValueHandling.Ignore)]
        public long? Cache { get; set; }

        [SchemaProperty(Description = "When true the sequence wraps to MinValue after reaching MaxValue instead of erroring.")]
        [JsonProperty(Order = 9)]
        public bool Cycle { get; set; }

        [JsonProperty(Order = 10, NullValueHandling = NullValueHandling.Ignore)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string VariantName { get; set; }

        [SchemaProperty(Description = "Optional. Stored as the MS_Description extended property. An omitted value leaves an existing description alone; \"\" removes it.")]
        [JsonProperty(Order = 12, NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }
    }
}
