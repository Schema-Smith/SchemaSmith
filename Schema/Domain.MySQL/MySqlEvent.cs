// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain.MySQL
{
    /// <summary>
    /// A MySQL/MariaDB scheduled event, declared rather than scripted.
    /// <para><b>What changes by declaring it.</b> As a scripted object an event was re-run on every deploy
    /// (DROP then CREATE), never diffed, and never removed when it left the package — so a retired event
    /// kept firing until someone dropped it by hand. Declared, it is compared and converges, and it is
    /// removed by absence like any other managed object.</para>
    /// <para><b>Scripted events still work.</b> A <c>.sql</c> file in the <c>Events/</c> folder is honoured
    /// exactly as before, so no existing package has to change. The two forms must not both declare the
    /// same event, which <c>--Validate</c> reports.</para>
    /// <para>Fields mirror <c>INFORMATION_SCHEMA.EVENTS</c>, which is the only catalog that reports these
    /// and is identical on MySQL and MariaDB. Events predate both supported floors, so there is no version
    /// gate.</para>
    /// </summary>
    public class MySqlEvent : DynamicBase
    {
        [SchemaProperty(Required = true, MaxLength = 64)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        /// <summary>
        /// The body after <c>DO</c>. A multi-statement body must be wrapped in <c>BEGIN … END</c> exactly as
        /// it would be in hand-written DDL — SchemaSmith does not add the wrapper, because doing so would
        /// change the semantics of a single-statement body that happens to contain a semicolon.
        /// </summary>
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public string Definition { get; set; } = "";

        /// <summary>
        /// <c>EVERY</c> (recurring) or <c>AT</c> (one-shot). Mirrors <c>EVENT_TYPE</c>, which reports
        /// RECURRING / ONE TIME.
        /// </summary>
        [SchemaProperty(Pattern = "EVERY|AT", Required = true)]
        [JsonProperty(Order = 3)]
        [DefaultValue("EVERY")]
        public string ScheduleType { get; set; } = "EVERY";

        /// <summary>For <c>EVERY</c>: the interval, e.g. <c>"1 DAY"</c> or <c>"30 MINUTE"</c>. Ignored for <c>AT</c>.</summary>
        [SchemaProperty(MaxLength = 64,
            Description = "For ScheduleType EVERY: the interval as a value and a unit, e.g. \"1 DAY\" or \"30 MINUTE\".")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public string Interval { get; set; }

        /// <summary>For <c>AT</c>: the single execution time. Ignored for <c>EVERY</c>.</summary>
        [SchemaProperty(MaxLength = 64,
            Description = "For ScheduleType AT: when the event runs, once, e.g. \"2027-06-01 12:00:00\".")]
        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public string ExecuteAt { get; set; }

        [SchemaProperty(MaxLength = 64, Description = "Optional start of the recurrence window (ScheduleType EVERY).")]
        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public string Starts { get; set; }

        [SchemaProperty(MaxLength = 64, Description = "Optional end of the recurrence window (ScheduleType EVERY).")]
        [JsonProperty(Order = 7, NullValueHandling = NullValueHandling.Ignore)]
        public string Ends { get; set; }

        /// <summary>
        /// ENABLE, DISABLE, or DISABLE ON SLAVE. The catalog spells these ENABLED / DISABLED /
        /// SLAVESIDE_DISABLED; the package uses the DDL spelling, because that is what an author writes.
        /// </summary>
        [SchemaProperty(Pattern = "ENABLE|DISABLE|DISABLE ON SLAVE")]
        [JsonProperty(Order = 8)]
        [DefaultValue("ENABLE")]
        public string Status { get; set; } = "ENABLE";

        /// <summary>
        /// When true the event survives its last run instead of dropping itself. Defaults FALSE to match
        /// the engine: MySQL's own default is NOT PRESERVE, and a one-shot event with PRESERVE off is
        /// removed by the server after it fires.
        /// </summary>
        [JsonProperty(Order = 9)]
        public bool Preserve { get; set; }

        [SchemaProperty(MaxLength = 2048)]
        [JsonProperty(Order = 10, NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string VariantName { get; set; }

        [JsonProperty(Order = 12, NullValueHandling = NullValueHandling.Ignore)]
        public string ShouldApplyExpression { get; set; }
    }
}
