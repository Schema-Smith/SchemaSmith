// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Capabilities
{
    /// <summary>
    /// How a version-gated feature's declared form behaves below its introduction version. Only features
    /// that genuinely change end-state below a version are catalogued (see <see cref="CapabilityRegistry"/>);
    /// a feature supported via a different mechanism with the same end-state is not a degrade and is excluded.
    /// </summary>
    public enum DegradeKind
    {
        /// <summary>The declared object is not created below the floor — e.g. a columnstore, JSON or selective XML
        /// index, a CHECK constraint, a VIRTUAL generated column.</summary>
        Skip,

        /// <summary>The object is created without what the server cannot express below the floor — e.g. a table
        /// without system versioning, a column unmasked, an index stored visible or ascending, a key without WITHOUT
        /// OVERLAPS.</summary>
        Reduced,

        /// <summary>No degrade is safe, so the deploy is refused below the floor whatever the unsupported-feature
        /// policy — e.g. a long unique key, where dropping the key would drop the uniqueness. Such a row has no
        /// manifest object type, since nothing is deployed to record.</summary>
        Refused
    }
}
