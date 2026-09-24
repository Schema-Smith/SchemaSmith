// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    /// <summary>
    /// A PostgreSQL enum type, declared rather than scripted.
    /// <para><b>This one fixes a silent no-op, not just an inconvenience.</b> As a scripted object an enum
    /// is created by a guarded <c>CREATE TYPE</c>. Once the type exists that guard skips — so editing the
    /// value list in the <c>.sql</c> file changes nothing, forever, and SchemaSmith never says so.
    /// Verified: re-running a guarded create with a third value left the type with its original two.
    /// Declared, the value list is compared and missing values are added.</para>
    /// <para><b>What can and cannot converge is the engine's limit, not a choice.</b> PostgreSQL can ADD a
    /// value (and place it, via <c>BEFORE</c>/<c>AFTER</c>), but it cannot remove or reorder one without
    /// dropping and recreating the type — which means dropping every column that uses it. So a value
    /// removed from the package is <i>reported</i>, never silently dropped, the same posture placement
    /// takes.</para>
    /// <para>Scripted enums still work: a <c>.sql</c> file in <c>Enum Types/</c> runs exactly as before.</para>
    /// </summary>
    public class PostgreSqlEnumType : DynamicBase
    {
        // See Product.SchemaRef. Root of its own file, so it carries the reference; the nested
        // types on DynamicBase deliberately do not.
        [SchemaProperty(Description = "Optional. Relative path to the generated JSON Schema, for editor validation and autocomplete. Ignored at deploy time.")]
        [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaRef { get; set; }

        [SchemaProperty(Required = true, MaxLength = 63)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        // Default resolution runs in SchemaDefaultResolver, matching materialized views: regular templates
        // default to "public", schema templates to "{{SchemaName}}".
        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        /// <summary>
        /// The labels, in order. Order is part of the type: PostgreSQL sorts and compares enum values by
        /// their declared position, not alphabetically, so a comparison that ignored order would call two
        /// genuinely different types equal.
        /// </summary>
        [SchemaProperty(Required = true,
            Description = "The enum's labels, in order. Order matters — PostgreSQL compares and sorts enum values by declared position. New labels are added in place; removing or reordering existing ones needs the type recreated, so SchemaSmith reports rather than attempts it.")]
        [JsonProperty(Order = 3)]
        public List<string> Values { get; set; } = [];

        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public string VariantName { get; set; }
    }
}
