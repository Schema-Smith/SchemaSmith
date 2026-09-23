// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Capabilities;
using Schema.Domain;

namespace Schema.UnitTests.Capabilities;

/// <summary>
/// Every catalogued degrade has to be written down where a user will actually meet it: the
/// <b>Engine Version Compatibility</b> section of <c>docs/end-user/reference/schemaquench.md</c>.
/// <para>This is the drift that already happened. The registry is the single source of truth for
/// "this authored feature needs a newer engine, and here is what happens below that", and the
/// reference page is the only place a user reads it — but nothing connected the two, so the page
/// fell to 16 of 27 rows before anyone counted. A user deploying to an older target does not read
/// <c>CapabilityRegistry.cs</c>; they read the table, find nothing about their feature, and conclude
/// it is safe.</para>
/// <para><b>Why the phrases live here and not on <see cref="Capability"/>.</b> How a feature is
/// *worded* for a reader is a documentation fact, not a domain one — the registry describes
/// behaviour and should not carry prose that exists to be matched. Keeping the map here also makes
/// the guard fail in the right direction: a new registry row has no entry, so the author is stopped
/// and has to decide what the docs should say, rather than being handed a default to ignore.</para>
/// <para><b>What this can and cannot prove.</b> It proves each degrade is mentioned in that section,
/// and — where the gate really is a version — that the version the docs quote is the one the
/// registry holds. It cannot prove the sentence next to it is *true*; that stays a human read. It
/// catches the failure that actually occurred (a shipped degrade nobody documented, and a documented
/// version that no longer matched the gate) and nothing subtler.</para>
/// </summary>
[TestFixture]
public class CapabilityDocumentationTests
{
    private static readonly string[] ReferencePage = ["docs", "end-user", "reference", "schemaquench.md"];
    private const string SectionHeading = "## Engine Version Compatibility";

    /// <summary>
    /// The phrase the reference page uses for each catalogued degrade, keyed as
    /// <c>platform/key</c> because the same key is documented once for both MySQL-family engines
    /// (a shared row reading "MySQL 8.0 / MariaDB 10.6") while carrying a distinct registry row per
    /// engine, since the thresholds differ.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DocumentedAs = new Dictionary<string, string>
    {
        // ---- SQL Server, version gated ----
        ["SqlServer/temporal"] = "**Temporal tables**",
        ["SqlServer/graph-table"] = "**Graph tables**",
        ["SqlServer/ledger-table"] = "**Ledger tables**",
        ["SqlServer/xml-compression"] = "**XML compression**",
        ["SqlServer/data-masking"] = "**Dynamic data masking**",
        ["SqlServer/always-encrypted"] = "**Always Encrypted**",
        ["SqlServer/columnstore-nonclustered"] = "**Nonclustered columnstore index**",
        ["SqlServer/columnstore-clustered"] = "**Clustered columnstore index**",

        // ---- SQL Server, gated on server or database STATE rather than version ----
        // These three are documented in a prose paragraph rather than the table, because "Requires"
        // would have to read "a setting, not a version" for all three. That is also why the
        // version-agreement half of this test skips them: IntroducedInComparable is 0.
        ["SqlServer/cdc-database-toggle"] = "**Change Data Capture**",
        ["SqlServer/change-tracking-database-toggle"] = "**Change Tracking**",
        ["SqlServer/filestream-column"] = "**FILESTREAM**",

        // ---- PostgreSQL ----
        ["PostgreSQL/nulls-not-distinct"] = "**`NULLS NOT DISTINCT`**",
        ["PostgreSQL/expression-statistics"] = "**Expression statistics**",
        ["PostgreSQL/column-compression"] = "**Per-column compression**",
        ["PostgreSQL/table-access-method"] = "**Table access method**",
        ["PostgreSQL/virtual-generated-column"] = "**`VIRTUAL` generated columns**",

        // ---- MySQL ----
        ["MySQL/invisible-index"] = "**Invisible index**",
        ["MySQL/descending-index"] = "**Descending index key parts**",
        ["MySQL/check-constraint"] = "**CHECK constraints**",
        ["MySQL/data-delivery"] = "**Automatic table-data delivery**",
        ["MySQL/default-expression"] = "**Column `DEFAULT` expression**",
        ["MySQL/invisible-column"] = "**Invisible column**",
        ["MySQL/column-srid"] = "**Column SRID restriction**",
        ["MySQL/functional-index"] = "**Functional / expression index**",

        // ---- MariaDB (shares three rows with MySQL above; the doc cell names both thresholds) ----
        ["MariaDb/invisible-index"] = "**Invisible index**",
        ["MariaDb/descending-index"] = "**Descending index key parts**",
        ["MariaDb/invisible-column"] = "**Invisible column**",
        ["MariaDb/application-time-period"] = "**Application-time period**",
        ["MariaDb/column-history-exclusion"] = "**Per-column history exclusion**",
        ["MariaDb/table-system-versioning"] = "**Table-level system versioning**",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>
    /// The lines of the compatibility section only. Scoping matters: several of these phrases also
    /// occur elsewhere on the page (and on other pages), so a whole-file search would report a
    /// degrade as documented because it is mentioned in passing somewhere a user comparing engine
    /// versions will never look.
    /// </summary>
    private static string[] CompatibilitySectionLines()
    {
        var root = RepoRoot();
        Assert.That(root, Is.Not.Null, "could not locate the repository root");

        var path = Path.Join(new[] { root }.Concat(ReferencePage).ToArray());
        Assert.That(File.Exists(path), Is.True, $"the reference page is missing at {path}");

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l.StartsWith(SectionHeading, StringComparison.Ordinal));
        Assert.That(start, Is.GreaterThanOrEqualTo(0),
            $"'{SectionHeading}' is gone from the reference page. Either it was renamed — update "
            + "SectionHeading here — or the section a user relies on for version degrades was removed.");

        // Ends at the next heading of the same level, so a renamed subsection cannot silently shrink
        // the window this searches.
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;

        return lines[start..end];
    }

    [TestCaseSource(nameof(EveryCapability))]
    public void EveryCataloguedDegradeIsDocumentedOnTheReferencePage(Capability capability)
    {
        var id = $"{capability.Platform}/{capability.Key}";

        Assert.That(DocumentedAs.ContainsKey(id), Is.True,
            $"The registry catalogues '{id}' ({capability.DisplayName}) but this test has no phrase "
            + "for it, which means nobody has decided how the reference page should describe it. Add "
            + "a row to the Engine Version Compatibility section of "
            + "docs/end-user/reference/schemaquench.md saying what the target does below the floor, "
            + "then map it here. A degrade users cannot read about is one they meet at deploy time.");

        var phrase = DocumentedAs[id];
        var section = CompatibilitySectionLines();
        var mentions = section.Where(l => l.Contains(phrase, StringComparison.Ordinal)).ToList();

        // A table row is the authoritative mention, and it is preferred over a prose one rather than
        // simply taken first: several degrades are ALSO named in the "what adapts at the floor"
        // paragraph that opens the MySQL section, which bolds the digits (`MySQL **8.0.16**`) for
        // emphasis. Matching that paragraph would compare the version against prose that was never
        // meant to be the machine-readable statement of the floor. The state-gated three have no
        // table row at all and legitimately fall through to prose -- their version check is skipped
        // below, because there is no version to agree with.
        var match = mentions.FirstOrDefault(l => l.StartsWith("|", StringComparison.Ordinal))
                    ?? mentions.FirstOrDefault();

        Assert.That(match, Is.Not.Null,
            $"'{id}' ({capability.DisplayName}) is catalogued as a degrade, but the phrase "
            + $"{phrase} appears nowhere in the Engine Version Compatibility section. Either the "
            + "documentation row was dropped, or its wording changed and this map did not follow.");

        // A version-gated degrade also has to quote the SAME threshold the gate compares against.
        // Rows with IntroducedInComparable 0 are gated on server or database state, so there is no
        // version for the page to agree with -- see the comment on the three SQL Server rows above.
        if (capability.IntroducedInComparable == 0) return;

        Assert.That(match, Does.Contain(capability.IntroducedInDisplay),
            $"The reference page documents '{id}' but does not quote the version the gate actually "
            + $"uses ('{capability.IntroducedInDisplay}'). A user reading a stale floor plans the "
            + $"wrong upgrade. The documented line reads: {match}");
    }

    /// <summary>
    /// The map cannot outlive the registry either. A phrase left behind after its row is removed
    /// would keep asserting against documentation for a degrade that no longer exists.
    /// </summary>
    [Test]
    public void TheDocumentationMapHasNoEntriesTheRegistryNoLongerCatalogues()
    {
        var catalogued = CapabilityRegistry.All
            .Select(c => $"{c.Platform}/{c.Key}")
            .ToHashSet(StringComparer.Ordinal);

        var orphaned = DocumentedAs.Keys.Where(k => !catalogued.Contains(k)).ToList();

        Assert.That(orphaned, Is.Empty,
            "These entries name capabilities the registry no longer holds: "
            + string.Join(", ", orphaned)
            + ". Remove them here, and remove the matching rows from the reference page unless the "
            + "feature is still degraded by some other means.");
    }

    private static IEnumerable<TestCaseData> EveryCapability() =>
        CapabilityRegistry.All.Select(c =>
            new TestCaseData(c).SetName($"{c.Platform}_{c.Key}".Replace('-', '_')));
}
