// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain
{
    /// <summary>
    /// Per-table policy controlling when SchemaQuench rebuilds a table (shadow table + data copy)
    /// instead of altering it in place. Declarable at the environment, product, template and table
    /// levels; the nearest declared level wins WHOLE (see ProductQuench.ResolveCascadedPolicy) —
    /// fields are never merged across levels.
    /// </summary>
    public class RebuildPolicy
    {
        [SchemaProperty(Pattern = "NEVER|ALWAYS|THRESHOLD",
            Description = "When to rebuild the table instead of altering it in place. NEVER (the default) always " +
                          "alters in place. ALWAYS rebuilds for any change. THRESHOLD rebuilds once the number of " +
                          "pending changes reaches Threshold, which is then required.")]
        [JsonProperty(Order = 1)]
        [DefaultValue("NEVER")]
        public string Mode { get; set; } = "NEVER";

        [SchemaProperty(Minimum = 1,
            Description = "Required when Mode is THRESHOLD: the number of pending changes at which the table is " +
                          "rebuilt rather than altered in place. Ignored for every other Mode.")]
        [JsonProperty(Order = 2)]
        public int? Threshold { get; set; }

        // Deliberately NOT a fourth Mode value. Triggers COMPOSE within a level, so a table must be able to
        // ask for "THRESHOLD: 3" AND order-mismatch at the same time; folding order-mismatch into the Mode
        // enum would force the author to pick one trigger or the other and make the combination
        // unexpressible.
        [SchemaProperty(Description = "Rebuild when the deployed column order does not match the authored column " +
                                      "order. An independent trigger that composes with Mode rather than replacing " +
                                      "it — a table can ask for both a threshold and an order-mismatch rebuild.")]
        [JsonProperty(Order = 3)]
        public bool OnOrderMismatch { get; set; }
    }
}
