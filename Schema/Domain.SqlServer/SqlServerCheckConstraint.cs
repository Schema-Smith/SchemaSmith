// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    public class SqlServerCheckConstraint : CheckConstraint
    {
        [SchemaProperty(Description = "false disables the constraint (ALTER TABLE ... NOCHECK CONSTRAINT): it stays defined but is not checked. Omitted means enforced, and a deploy re-enables a constraint someone disabled.")]
        [JsonProperty(Order = 100)]
        [DefaultValue(true)]
        public bool Enforced { get; set; } = true;

        [SchemaProperty(Description = "false creates the constraint WITH NOCHECK, so the rows already in the table are not checked and the constraint is untrusted. true over an untrusted constraint re-checks those rows, which fails when any violate it. Omitted means validated.")]
        [JsonProperty(Order = 101)]
        [DefaultValue(true)]
        public bool Validated { get; set; } = true;

        [SchemaProperty(Description = "NOT FOR REPLICATION: the constraint is not checked when a replication agent applies changes. Create-time only, so a change drops and re-creates the constraint.")]
        [JsonProperty(Order = 102)]
        [DefaultValue(false)]
        public bool NotForReplication { get; set; }
    }
}
