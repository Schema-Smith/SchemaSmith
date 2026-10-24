// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    public class SqlServerCheckConstraint : CheckConstraint
    {
        [SchemaProperty(Description = "false disables the constraint (ALTER TABLE ... NOCHECK CONSTRAINT): it stays defined but is not checked. Omitted leaves an existing constraint as it is, and a new one is enforced.")]
        [JsonProperty(Order = 100, NullValueHandling = NullValueHandling.Ignore)]
        public bool? Enforced { get; set; }

        [SchemaProperty(Description = "false creates the constraint WITH NOCHECK, so the rows already in the table are not checked and the constraint is untrusted. true over an untrusted constraint re-checks those rows, which fails when any violate it. Omitted leaves an existing constraint as it is, and a new one is validated.")]
        [JsonProperty(Order = 101, NullValueHandling = NullValueHandling.Ignore)]
        public bool? Validated { get; set; }

        [SchemaProperty(Description = "NOT FOR REPLICATION: the constraint is not checked when a replication agent applies changes. Create-time only, so a change drops and re-creates the constraint.")]
        [JsonProperty(Order = 102, NullValueHandling = NullValueHandling.Ignore)]
        public bool? NotForReplication { get; set; }
    }
}
