// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable

using System.Collections.Generic;
using Schema.Domain;

namespace Schema.Capabilities
{
    /// <summary>
    /// One authorable engine feature that SchemaSmith degrades (skips or applies with reduced fidelity) below
    /// a given engine version. Informational metadata: SchemaSmith's own deploy-time guards are the behavior;
    /// this record documents them so a consumer can tell, without a live connection, that an authored feature
    /// needs a newer engine than a given target. Rows are keyed per <em>exact</em> platform (MariaDb rows are
    /// distinct from MySQL — the two have different introduction versions).
    /// </summary>
    /// <param name="Key">Stable identifier for the feature, unique within a platform (e.g. "invisible-index").</param>
    /// <param name="DisplayName">Human-readable feature name.</param>
    /// <param name="Platform">The exact platform this row describes.</param>
    /// <param name="IntroducedInComparable">
    /// The introduction version in the engine's comparable encoding — SQL Server / PostgreSQL major
    /// (13, 15), MySQL / MariaDb <c>major*100 + minor</c> (800, 1006, 1008). Matches the literal the deploy-time
    /// <c>.sql</c> guard compares against, so a consistency test can assert the two never drift apart.
    /// </param>
    /// <param name="IntroducedInDisplay">Human-readable version string (e.g. "SQL Server 2016", "MariaDB 10.8").</param>
    /// <param name="RequiredCompatLevel">
    /// The SQL Server database compatibility level required when the feature is compat-gated rather than
    /// binary/version-gated; null otherwise. All current rows are version-gated, so this is null — the field
    /// exists for features (e.g. certain syntax) whose availability tracks compatibility level.
    /// </param>
    /// <param name="RequiredDatabaseSetting">
    /// A DATABASE-scoped prerequisite the feature needs and SchemaSmith does not own (e.g. CDC enabled on
    /// the database). Null for the version-gated majority. When set, the degrade does not depend on the
    /// engine version at all, so <see cref="IntroducedInComparable"/> is 0 and means "not version-gated"
    /// rather than "unknown" -- a row must not invent a version its .sql guard never compares against.
    /// </param>
    /// <param name="Degrade">Whether the feature is skipped or applied with reduced fidelity below the floor.</param>
    /// <param name="ManifestObjectType">
    /// The exact <c>ChangeAudit</c> "downgraded" ObjectType string when the degrade routes through
    /// <c>UnsupportedFeaturePolicy</c> (the "cite by message text" anchor); null for behavior-based degrades
    /// that produce no manifest row.
    /// </param>
    /// <param name="IntroducedInPatchComparable">
    /// For a gate that turns on a patch (the MySQL family's <c>major*10000+minor*100+patch</c>): the patch the feature
    /// arrived in, which <see cref="IntroducedInComparable"/> (major*100+minor) cannot express. Null when the
    /// major/minor comparable is exact.
    /// </param>
    /// <param name="IntroducedInPatchByLine">
    /// For a fix the engine shipped on several release lines at once: the first fixed patch on each line, in the same
    /// encoding, oldest line first (MariaDB 10.2.37, 10.3.28, 10.4.18, 10.5.9 for one fix). A server is covered when its
    /// line's entry is met; a line newer than the last entry is covered from its first patch. <see cref="IntroducedInPatchComparable"/>
    /// holds the oldest entry. Null for a feature that arrived once.
    /// </param>
    public sealed record Capability(
        string Key,
        string DisplayName,
        Platform Platform,
        int IntroducedInComparable,
        string IntroducedInDisplay,
        int? RequiredCompatLevel,
        DegradeKind Degrade,
        string? ManifestObjectType,
        string? RequiredDatabaseSetting = null,
        int? IntroducedInPatchComparable = null,
        IReadOnlyList<int>? IntroducedInPatchByLine = null);
}
