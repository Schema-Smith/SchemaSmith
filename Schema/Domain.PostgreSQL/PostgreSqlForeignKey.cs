// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Delivery;
using System.ComponentModel;

namespace Schema.Domain.PostgreSQL
{
    public class PostgreSqlForeignKey : ForeignKey, IDeliverableForeignKey
    {
        // Default resolution moved to SchemaDefaultResolver. Regular templates: defaults to "public".
        // Schema templates: defaults to "{{SchemaName}}"; explicit literals are preserved as
        // cross-schema references (e.g. tenant FK referencing public.countries).
        [JsonProperty(Order = 100, NullValueHandling = NullValueHandling.Ignore)]
        public string RelatedTableSchema { get; set; }

        [JsonProperty(Order = 101)]
        public bool Deferrable { get; set; }

        [JsonProperty(Order = 102)]
        public bool InitiallyDeferred { get; set; }

        [SchemaProperty(Pattern = "FULL|PARTIAL|SIMPLE", Description = "How a multi-column key with some NULL columns is matched. Omitted means SIMPLE, the engine's own default: a row with any NULL key column is not checked. FULL rejects a row whose key is only partly NULL.")]
        [JsonProperty(Order = 103)]
        [DefaultValue("SIMPLE")]
        public string MatchType { get; set; } = "SIMPLE";

        /// <summary>
        /// <c>ON DELETE SET NULL (column, ...)</c> (PostgreSQL 15+): with <see cref="ForeignKey.DeleteAction"/> <c>SET NULL</c>, only
        /// these of the key's <see cref="ForeignKey.Columns"/> are set to NULL when the referenced row is deleted; the others
        /// keep their values. Omitted means every column, as a plain <c>SET NULL</c> does. Below 15 every column is set
        /// and a downgrade is recorded.
        /// </summary>
        [SchemaProperty(Description = "PostgreSQL 15+. With DeleteAction SET NULL, the comma-separated subset of Columns set to NULL when the referenced row is deleted (ON DELETE SET NULL (column, ...)); the other key columns keep their values. Omitted means every column. Below 15 every column is set and a downgrade is recorded.")]
        [JsonProperty(Order = 106, NullValueHandling = NullValueHandling.Ignore)]
        public string SetNullColumns { get; set; }

        [SchemaProperty(Description = "false declares the key NOT ENFORCED (PostgreSQL 18): it is kept but not checked. Below 18 it deploys enforced under UnsupportedFeaturePolicy. Omitted means enforced, and a deploy enforces a key someone stopped enforcing.")]
        [JsonProperty(Order = 104)]
        [DefaultValue(true)]
        public bool Enforced { get; set; } = true;

        [SchemaProperty(Description = "false creates the key NOT VALID, so the rows already in the table are not checked. true over a NOT VALID key validates it, which fails when any row violates it. Omitted means validated.")]
        [JsonProperty(Order = 105)]
        [DefaultValue(true)]
        public bool Validated { get; set; } = true;
    }
}
