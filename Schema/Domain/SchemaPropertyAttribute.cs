// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace Schema.Domain
{
    [AttributeUsage(AttributeTargets.Property)]
    public class SchemaPropertyAttribute : Attribute
    {
        /// <summary>
        /// A JSON Schema <c>pattern</c> for this property. Declare it UNANCHORED and as a plain
        /// alternation (<c>A|B|C</c>) — <see cref="Schema.Utility.SchemaGenerator"/> anchors it on the way
        /// out. JSON Schema patterns are partial matches (draft 2020-12 §6.3.3), so an unanchored
        /// <c>NEVER|ALWAYS</c> accepts <c>XNEVERY</c>, and <c>Y|N</c> accepts any string containing a Y or
        /// an N. Anchoring centrally rather than per declaration is deliberate: 21 of 25 declarations were
        /// written without anchors, which says the next one will be too.
        /// </summary>
        public string Pattern { get; set; }

        /// <summary>
        /// Set when the PRODUCT reads this property case-insensitively, so the emitted pattern accepts the
        /// same casings a deploy does. Without it the linter contradicts the product: <c>MergeType</c>
        /// compares <c>OrdinalIgnoreCase</c> and <c>RebuildPolicy.Mode</c> upper-cases before comparing, so
        /// both accept <c>never</c> / <c>insert/update</c> while a case-sensitive pattern rejected them.
        /// <para>
        /// Opt-in per property BECAUSE IT MUST BE MEASURED, not assumed: only these two had their
        /// product-side reads read. Applying case-folding to a property the product compares exactly would
        /// be the same class of mistake in the other direction. Valid only on a plain literal alternation.
        /// </para>
        /// </summary>
        public bool PatternIgnoreCase { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public double MultipleOf { get; set; } = double.NaN;
        public string Format { get; set; }
        public int MaxLength { get; set; } = -1;
        public string Description { get; set; }
        /// <summary>
        /// Marks a property that extraction cannot reconstruct because it is authored intent rather than
        /// something the catalog knows -- a deploy-behaviour switch, say. SchemaTongs copies every marked
        /// property forward from the file it is overwriting, and a completeness test requires each
        /// table/component property to be either reachable by an extractor or marked here, so a new one
        /// cannot silently start disappearing on re-extract.
        /// </summary>
        public bool AuthoredOnly { get; set; }

        public bool Deprecated { get; set; }
        public bool Required { get; set; }

        // Name of a sibling bool property that makes Required conditional: the property is required
        // UNLESS that bool is true. Emitted as a JSON Schema if/else so editors keep flagging the
        // ordinary case. Ignored on types that do not declare the named property, where the plain
        // Required applies -- an engine without the concept has no exception to make.
        public string RequiredUnless { get; set; }
        public bool SingleOrArray { get; set; }

        /// <summary>
        /// The engines this property applies to. Empty (the default) means every engine, so scoping is
        /// opt-in and an undecorated property behaves exactly as it always has.
        /// <para><b>Why an attribute rather than a platform subclass.</b> Template and Product are shared
        /// types. Moving a single-engine setting onto SqlServerTemplate works, but a setting that applies
        /// to <i>two</i> engines -- DropStatisticsRemovedFromProduct and UpdateFillFactor apply to SQL
        /// Server and PostgreSQL but not MySQL/MariaDB -- would have to be declared in two subclasses,
        /// with two JsonProperty Orders to keep in sync. Product has no subclasses at all, and MariaDB
        /// shares MySqlTemplate, so a MariaDB-only setting would have nowhere to live. One attribute
        /// expresses all of those.</para>
        /// <para>This scopes what is EMITTED into the .json-schema files. The property still exists on the
        /// class, so nothing at deploy time changes; because the generated schemas carry
        /// additionalProperties:false, setting a property on the wrong platform becomes a schema
        /// violation that --Validate already reports -- no separate rule is needed for it.</para>
        /// </summary>
        public Platform[] Platforms { get; set; } = [];
    }
}
