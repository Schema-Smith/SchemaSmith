// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Domain
{
    /// <summary>
    /// The terminal verdict for a single script execution. <see cref="Skipped"/> is the
    /// sentinel "should not apply" outcome — a success that did not apply changes, distinct
    /// from <see cref="Applied"/> and <see cref="Failed"/> so the deployment summary report
    /// (#243) can report it honestly.
    /// </summary>
    public enum ScriptOutcome
    {
        Applied,
        Skipped,
        Failed,
        /// <summary>Not re-run: the script text and the module the server renders are both as last applied.</summary>
        Unchanged
    }
}
