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

        [SchemaProperty(Pattern = "FULL|PARTIAL|SIMPLE")]
        [JsonProperty(Order = 103)]
        [DefaultValue("FULL")]
        public string MatchType { get; set; } = "FULL";
    }
}
