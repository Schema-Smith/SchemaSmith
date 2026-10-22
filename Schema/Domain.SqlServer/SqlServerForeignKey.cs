// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;
using Schema.Delivery;

namespace Schema.Domain.SqlServer
{
    public class SqlServerForeignKey : ForeignKey, IDeliverableForeignKey
    {
        // Default resolution moved to SchemaDefaultResolver. Regular templates: defaults to "dbo".
        // Schema templates: defaults to "{{SchemaName}}"; explicit literals are preserved as
        // cross-schema references (e.g. tenant FK referencing dbo.Countries).
        [JsonProperty(Order = 100, NullValueHandling = NullValueHandling.Ignore)]
        public string RelatedTableSchema { get; set; }

        [SchemaProperty(Description = "false disables the key (ALTER TABLE ... NOCHECK CONSTRAINT): it stays defined but is not checked. Omitted means enforced, and a deploy re-enables a key someone disabled.")]
        [JsonProperty(Order = 101)]
        [DefaultValue(true)]
        public bool Enforced { get; set; } = true;

        [SchemaProperty(Description = "false creates the key WITH NOCHECK, so the rows already in the table are not checked and the key is untrusted. true over an untrusted key re-checks those rows, which fails when any violate it. Omitted means validated.")]
        [JsonProperty(Order = 102)]
        [DefaultValue(true)]
        public bool Validated { get; set; } = true;

        [SchemaProperty(Description = "NOT FOR REPLICATION: the key is not checked when a replication agent applies changes. Create-time only, so a change drops and re-creates the key.")]
        [JsonProperty(Order = 103)]
        [DefaultValue(false)]
        public bool NotForReplication { get; set; }
    }
}
