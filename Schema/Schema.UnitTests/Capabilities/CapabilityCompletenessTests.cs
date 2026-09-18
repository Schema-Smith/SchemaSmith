// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Schema.Capabilities;

namespace Schema.UnitTests.Capabilities
{
    // Completeness guard for the MySQL family: CapabilityGuardConsistencyTests (integration) is one-way — it
    // walks EXISTING registry rows and asserts each row's live predicate flips at IntroducedInComparable, but
    // a gate with no row at all is invisible to it. That is exactly how four SchemaSmith_Supports* gates
    // (functional index, default expression, invisible column, column SRID) shipped on this branch with a
    // fully green gate and no registry row. This test closes the other direction: every embedded
    // SchemaSmith_Supports*.sql script must be accounted for, either by a registry row or by an explicit,
    // reasoned entry in the DeliberatelyNotADegrade dictionary below.
    [TestFixture]
    public class CapabilityCompletenessTests
    {
        private static readonly Assembly SchemaAsm = typeof(Capability).Assembly;

        // Gate script -> the CapabilityRegistry.Key it must have at least one row for. Hand-mapped rather
        // than derived from the filename (SupportsCheckConstraints -> "check-constraint" is singular, not
        // the plural a naive kebab-case transform would produce), so the correspondence is explicit and a
        // rename on either side breaks this test instead of silently drifting apart.
        private static readonly IReadOnlyDictionary<string, string> GateRegistryKey = new Dictionary<string, string>
        {
            ["SchemaSmith_SupportsCheckConstraints.sql"] = "check-constraint",
            ["SchemaSmith_SupportsDescendingIndex.sql"] = "descending-index",
            ["SchemaSmith_SupportsInvisibleIndex.sql"] = "invisible-index",
            ["SchemaSmith_SupportsDefaultExpression.sql"] = "default-expression",
            ["SchemaSmith_SupportsInvisibleColumn.sql"] = "invisible-column",
            ["SchemaSmith_SupportsApplicationTimePeriods.sql"] = "application-time-period",
            ["SchemaSmith_SupportsColumnSrid.sql"] = "column-srid",
            ["SchemaSmith_SupportsFunctionalIndex.sql"] = "functional-index",
            ["SchemaSmith_SupportsSystemVersioning.sql"] = "column-history-exclusion",
        };

        // Gates whose predicate is real but deliberately carries NO registry row. CapabilityRegistry's own
        // summary states the inclusion rule: "a feature is listed when its declared form is actually skipped
        // or applied with reduced fidelity below a version." A feature reached a different way with the same
        // end-state fails that rule one direction (Rename*); a feature with no accepted end-state below the
        // floor at all fails it the other direction (FunctionalIndex). Both are exclusions, not oversights —
        // but only once someone has said so here.
        private static readonly IReadOnlyDictionary<string, string> DeliberatelyNotADegrade = new Dictionary<string, string>
        {
            ["SchemaSmith_SupportsRenameColumn.sql"] =
                "Below the floor a rename is emitted as CHANGE COLUMN old new <current-definition> — same " +
                "end-state via a different mechanism, not a degrade (registry inclusion rule).",

            ["SchemaSmith_SupportsRenameIndex.sql"] =
                "Below the floor a rename is emitted as drop+recreate — same end-state via a different " +
                "mechanism, not a degrade (registry inclusion rule).",
        };

        [Test]
        public void EveryMySqlFamilySupportsGate_HasARegistryRowOrADocumentedExclusion()
        {
            var gates = SchemaAsm.GetManifestResourceNames()
                .Where(n => n.Contains("SchemaSmith_Supports", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                .Select(BareFileName)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.That(gates, Is.Not.Empty,
                "no SchemaSmith_Supports*.sql gates found as embedded resources — resource discovery is broken, fix before trusting this test");

            var problems = new List<string>();
            foreach (var gate in gates)
            {
                if (DeliberatelyNotADegrade.ContainsKey(gate))
                    continue;

                if (!GateRegistryKey.TryGetValue(gate, out var key))
                {
                    problems.Add($"{gate}: unrecognised gate — add a CapabilityRegistry row (and a GateRegistryKey entry " +
                                 "here), or a DeliberatelyNotADegrade entry with a stated reason");
                    continue;
                }

                if (!CapabilityRegistry.All.Any(c => c.Key == key))
                    problems.Add($"{gate}: mapped to registry key '{key}' but CapabilityRegistry.All has no row for it — add one");
            }

            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        // The Supports*.sql sweep above only sees gates written as a gate FUNCTION. A degrade written as an
        // inline version comparison has no such file and is invisible to it -- which is how PostgreSQL's
        // VIRTUAL generated-column degrade shipped in v2.5.0 with no registry row, fully policy-routed and
        // completely absent from the list downstream tooling drives its behaviour from.
        //
        // This closes that hole by keying on BEHAVIOUR instead of file naming: every degrade, however it is
        // gated, records a 'downgraded' ChangeAudit row, and the ObjectType it writes is the string the
        // registry calls ManifestObjectType. Any script writing a downgrade for a type the registry does not
        // know about is a gate nobody catalogued.
        [Test]
        public void EveryDowngradeWrittenBySqlHasARegistryRow()
        {
            var known = CapabilityRegistry.All
                .Select(c => c.ManifestObjectType)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToHashSet(StringComparer.Ordinal);

            // The session-id call is the reliable anchor: every engine writes ChangeAudit as
            // (<session id>, '<ObjectType>', <name>, '<action>'), so the literal straight after it is the type.
            var typeAfterSessionId = new Regex(
                @"(?:@@SPID|pg_backend_pid\(\)|CONNECTION_ID\(\))\s*,\s*'([^']+)'",
                RegexOptions.IgnoreCase);

            var problems = new List<string>();
            foreach (var resource in SchemaAsm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = SchemaAsm.GetManifestResourceStream(resource);
                if (stream == null) continue;
                var sql = new StreamReader(stream).ReadToEnd();
                // Strip line comments first. These scripts explain their own behaviour in prose, so a
                // comment saying "records one 'downgraded' row" would otherwise be read as a downgrade and
                // attributed to whatever literal happened to precede it.
                sql = Regex.Replace(sql, @"--[^
]*", "");
                if (!sql.Contains("'downgraded'", StringComparison.Ordinal)) continue;

                // Scope to the individual INSERT that records the downgrade -- ChangeAudit also records
                // ordinary creates and drops through the same shape, and those are not degrades.
                foreach (var chunk in Regex.Split(sql, @"INSERT\s+INTO", RegexOptions.IgnoreCase))
                {
                    if (!chunk.Contains("ChangeAudit", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!chunk.Contains("'downgraded'", StringComparison.Ordinal)) continue;

                    var match = typeAfterSessionId.Match(chunk);
                    if (!match.Success) continue;
                    var objectType = match.Groups[1].Value;
                    if (objectType == "downgraded" || known.Contains(objectType)) continue;

                    problems.Add(
                        $"{BareFileName(resource)} records a downgrade as '{objectType}', which no CapabilityRegistry "
                        + "row claims — add a row (ManifestObjectType must match this string exactly) so a consumer "
                        + "can see the degrade");
                }
            }

            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        private static string BareFileName(string resourceName)
        {
            var parts = resourceName.Split('.');
            return $"{parts[^2]}.{parts[^1]}";
        }
    }
}
