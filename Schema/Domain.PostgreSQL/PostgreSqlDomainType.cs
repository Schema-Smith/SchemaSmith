// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    /// <summary>
    /// A PostgreSQL domain type, declared rather than scripted (F5).
    /// <para><b>It is promoted because it has storage</b> — real columns are typed by it, which is the test
    /// for whether the declarative model earns its cost. A scripted object re-runs unconditionally on every
    /// deploy, and for a procedure or a rule that is cheap; for something columns depend on it is not.</para>
    /// <para><b>And the scripted form here cannot be made idempotent at all.</b> There is no
    /// <c>CREATE OR REPLACE DOMAIN</c>, so a scripted domain is a guarded <c>CREATE DOMAIN</c> — and once
    /// the domain exists that guard skips. Verified on a live server: re-running a guarded create carrying
    /// <c>CHECK (VALUE &gt; 100)</c> left the domain with its original <c>CHECK (VALUE &gt; 0)</c>, silently,
    /// with the deploy reporting success. That is the same trap the enum promotion closed.</para>
    /// <para><b>What converges, and what is refused.</b> <c>ALTER DOMAIN</c> can add and drop constraints,
    /// set and drop the default, and set and drop NOT NULL — all without dropping the domain or touching a
    /// single dependent column. The base type is the exception: there is no <c>ALTER DOMAIN … TYPE</c> at
    /// all (a syntax error, verified), so changing it would mean dropping the domain and every column using
    /// it. That is refused by name and left to a migration script.</para>
    /// </summary>
    public class PostgreSqlDomainType : DynamicBase
    {
        // See Product.SchemaRef. Root of its own file, so it carries the reference; the nested
        // types on DynamicBase deliberately do not.
        [SchemaProperty(Description = "Optional. Relative path to the generated JSON Schema, for editor validation and autocomplete. Ignored at deploy time.")]
        [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaRef { get; set; }

        [SchemaProperty(Required = true, MaxLength = 63)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        /// <summary>
        /// The underlying type, with its modifier where it has one — <c>integer</c>,
        /// <c>character varying(20)</c>, <c>numeric(10,2)</c>. Extraction emits exactly what
        /// <c>format_type</c> reports, so a round-tripped package redeploys the same type.
        /// <para><b>Create-time only.</b> PostgreSQL has no <c>ALTER DOMAIN … TYPE</c>, so a declared base
        /// type that differs from the deployed one is refused by name rather than attempted — changing it
        /// requires dropping the domain, which drops every column that uses it.</para>
        /// </summary>
        [SchemaProperty(Required = true,
            Description = "The underlying type, with its modifier where it has one (integer, character varying(20)). Create-time only: PostgreSQL has no ALTER DOMAIN ... TYPE, so a change is refused rather than attempted.")]
        [JsonProperty(Order = 3)]
        public string DataType { get; set; } = "";

        /// <summary>
        /// Converges in place via <c>ALTER DOMAIN … SET/DROP NOT NULL</c>. Setting it on a domain whose
        /// columns already hold NULLs is refused by the engine, with its own message naming the offending
        /// column — which is the right error, so nothing here pre-empts it.
        /// </summary>
        [JsonProperty(Order = 4)]
        public bool NotNull { get; set; }

        /// <summary>
        /// The domain's default expression, or null for none. Converges via
        /// <c>ALTER DOMAIN … SET/DROP DEFAULT</c>.
        /// </summary>
        [SchemaProperty(Description = "Default expression applied to a column of this domain that declares no default of its own. Converges in place.")]
        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public string Default { get; set; }

        /// <summary>
        /// The domain's CHECK constraints, converged as a set — added when the package declares one the
        /// server lacks, dropped when the server has one the package no longer declares.
        /// <para>Dropping one is safe in a way dropping an enum value is not: it removes a validation rule,
        /// destroys no data, and cascades to nothing. That asymmetry is the whole reason this type
        /// converges where the enum reports.</para>
        /// <para>Adding one VALIDATES the existing data and fails loudly if any row violates it. That is the
        /// engine protecting the user, so it is surfaced rather than worked around.</para>
        /// </summary>
        [JsonProperty(Order = 6)]
        public List<PostgreSqlDomainConstraint> CheckConstraints { get; set; } = [];

        [JsonProperty(Order = 10, NullValueHandling = NullValueHandling.Ignore)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string VariantName { get; set; }
    }

    /// <summary>
    /// One CHECK constraint on a domain.
    /// <para>The name is required and is the identity used for comparison. PostgreSQL generates one
    /// (<c>&lt;domain&gt;_check</c>, then <c>_check1</c>, …) when a constraint is declared without a name,
    /// so extraction always emits a name and a hand-authored package must supply one — comparing on the
    /// expression instead would make a reformatted expression look like a different constraint.</para>
    /// </summary>
    public class PostgreSqlDomainConstraint : DynamicBase
    {
        [SchemaProperty(Required = true, MaxLength = 63)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        /// <summary>
        /// The predicate, referring to the value under test as <c>VALUE</c> — e.g. <c>VALUE &gt; 0</c>.
        /// Written without the surrounding <c>CHECK (…)</c>.
        /// </summary>
        [SchemaProperty(Required = true,
            Description = "The predicate, using VALUE for the value under test (VALUE > 0). Without the surrounding CHECK (...).")]
        [JsonProperty(Order = 2)]
        public string Expression { get; set; } = "";
    }
}
