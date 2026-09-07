// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain.PostgreSQL
{
    /// <summary>
    /// A PostgreSQL row-level security policy (gap item D1).
    /// <para><b>Why this matters more than a missing convenience.</b> SchemaSmith could already turn row
    /// level security ON via <c>RowLevelSecurity</c>, but had no way to declare a policy — and a table with
    /// RLS enabled and no policy returns <b>no rows at all</b> to anyone but its owner. Verified against a
    /// live server: enable RLS, grant SELECT, read as the grantee, get zero. So the half that shipped could
    /// lock a table with no supported way to unlock it.</para>
    /// <para>Policies are table components, so they follow the same drop-by-absence rule as check and
    /// exclude constraints: removing one from the package removes it from the database. That is the correct
    /// posture here precisely because a stale policy is a live access-control rule.</para>
    /// </summary>
    public class PostgreSqlPolicy : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        /// <summary>
        /// PERMISSIVE (default) policies are OR-ed together; RESTRICTIVE policies are AND-ed on top. A
        /// table with only RESTRICTIVE policies still returns nothing, because there is no permissive
        /// policy to grant anything in the first place.
        /// </summary>
        [SchemaProperty(Pattern = "PERMISSIVE|RESTRICTIVE")]
        [JsonProperty(Order = 2)]
        [DefaultValue("PERMISSIVE")]
        public string Permissive { get; set; } = "PERMISSIVE";

        [SchemaProperty(Pattern = "ALL|SELECT|INSERT|UPDATE|DELETE")]
        [JsonProperty(Order = 3)]
        [DefaultValue("ALL")]
        public string Command { get; set; } = "ALL";

        /// <summary>
        /// Comma-separated role list. Defaults to <c>PUBLIC</c>, matching PostgreSQL's own default when
        /// <c>TO</c> is omitted. Roles are not created by SchemaSmith — a policy naming a role that does
        /// not exist fails with PostgreSQL's own error.
        /// </summary>
        [JsonProperty(Order = 4)]
        [DefaultValue("PUBLIC")]
        public string Roles { get; set; } = "PUBLIC";

        /// <summary>
        /// The <c>USING</c> expression — which existing rows are visible. Omitted for an INSERT-only
        /// policy, where PostgreSQL does not accept one.
        /// </summary>
        [JsonProperty(Order = 5)]
        public string UsingExpression { get; set; }

        /// <summary>
        /// The <c>WITH CHECK</c> expression — which new or updated rows are allowed. When omitted on a
        /// policy that has <c>USING</c>, PostgreSQL applies the USING expression to writes as well.
        /// </summary>
        [JsonProperty(Order = 6)]
        public string WithCheckExpression { get; set; }

        [JsonProperty(Order = 7)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 8)]
        public string VariantName { get; set; }
    }
}
