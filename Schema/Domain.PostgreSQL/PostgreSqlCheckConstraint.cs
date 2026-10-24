// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    public class PostgreSqlCheckConstraint : CheckConstraint
    {
        // TRANSITIONAL (deprecated in 2.8): PostgreSQL has never accepted DEFERRABLE on a CHECK constraint, so no deployed
        // package can have used these. The deploy no longer emits them and --Validate stops a package that sets one
        // (SS-CHK-001). Delete both properties two releases past 2.8 GA -- roadmap entry "The deferrable CHECK keys".
        [SchemaProperty(Deprecated = true, Description = "Deprecated: PostgreSQL rejects DEFERRABLE on a CHECK constraint, and the deploy no longer emits it. Remove the key.")]
        [JsonProperty(Order = 100)]
        public bool Deferrable { get; set; }

        [SchemaProperty(Deprecated = true, Description = "Deprecated: PostgreSQL rejects INITIALLY DEFERRED on a CHECK constraint, and the deploy no longer emits it. Remove the key.")]
        [JsonProperty(Order = 101)]
        public bool InitiallyDeferred { get; set; }

        [SchemaProperty(Description = "false declares the constraint NOT ENFORCED (PostgreSQL 18): it is kept but not checked. Below 18 it deploys enforced under UnsupportedFeaturePolicy. Omitted leaves an existing constraint as it is, and a new one is enforced. PostgreSQL cannot alter a check constraint's enforceability, so a change drops and re-creates it.")]
        [JsonProperty(Order = 102, NullValueHandling = NullValueHandling.Ignore)]
        public bool? Enforced { get; set; }

        [SchemaProperty(Description = "false creates the constraint NOT VALID, so the rows already in the table are not checked. true over a NOT VALID constraint validates it, which fails when any row violates it. Omitted leaves an existing constraint as it is, and a new one is validated.")]
        [JsonProperty(Order = 103, NullValueHandling = NullValueHandling.Ignore)]
        public bool? Validated { get; set; }

        [SchemaProperty(Description = "NO INHERIT: the constraint is not inherited by child tables. Create-time only, so a change drops and re-creates the constraint.")]
        [JsonProperty(Order = 104, NullValueHandling = NullValueHandling.Ignore)]
        public bool? NoInherit { get; set; }
    }
}
