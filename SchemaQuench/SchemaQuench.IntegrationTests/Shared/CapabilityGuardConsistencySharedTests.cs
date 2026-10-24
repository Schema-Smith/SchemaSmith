// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.Capabilities;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Drift-guard for the capability registry against the LIVE MySQL/MariaDb guard predicates. Each registry
/// row whose feature is gated by a <c>SchemaSmith_Supports*</c> function must have that predicate flip
/// exactly at the row's <see cref="Capability.IntroducedInComparable"/> — one below returns 0, at-or-above
/// returns 1 — probed via the <c>@schemasmith_version_override</c> session affordance. If a <c>.sql</c> gate
/// version ever changes without the registry being updated (or vice-versa), this fails. MariaDb rows carry
/// their own (distinct) intro versions, so running this under both the MySQL and MariaDb fixtures asserts
/// each engine's boundary independently.
/// </summary>
public abstract class CapabilityGuardConsistencySharedTests : BaseTableQuenchTests
{
    // Registry key -> the live MySQL-family predicate function that gates it.
    private static readonly Dictionary<string, string> PredicateFnByKey = new()
    {
        ["invisible-index"] = "SchemaSmith_SupportsInvisibleIndex",
        ["descending-index"] = "SchemaSmith_SupportsDescendingIndex",
        ["check-constraint"] = "SchemaSmith_SupportsCheckConstraints",
        ["default-expression"] = "SchemaSmith_SupportsDefaultExpression",
        ["invisible-column"] = "SchemaSmith_SupportsInvisibleColumn",
        ["column-srid"] = "SchemaSmith_SupportsColumnSrid",
        ["functional-index"] = "SchemaSmith_SupportsFunctionalIndex",
        ["generated-column-collation"] = "SchemaSmith_SupportsGeneratedColumnCollation"
    };

    [Test]
    public void SupportsPredicates_FlipAtRegistryIntroducedInComparable()
    {
        var rows = CapabilityRegistry.For(Platform).Where(c => PredicateFnByKey.ContainsKey(c.Key)).ToList();
        Assert.That(rows, Is.Not.Empty, $"expected at least one predicate-gated capability row for {Platform}");

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            Assert.Multiple(() =>
            {
                foreach (var cap in rows)
                {
                    var fn = PredicateFnByKey[cap.Key];
                    // A gate that turns on a patch is probed one patch below and at it; the others one minor below and at it.
                    var at = cap.IntroducedInPatchComparable ?? cap.IntroducedInComparable;
                    var patched = cap.IntroducedInPatchComparable != null;
                    Assert.That(Probe(cmd, fn, at - 1, patched), Is.EqualTo(0),
                        $"{fn}() must be 0 just below {cap.IntroducedInDisplay} ({at}) on {Platform}");
                    Assert.That(Probe(cmd, fn, at, patched), Is.EqualTo(1),
                        $"{fn}() must be 1 at {cap.IntroducedInDisplay} ({at}) on {Platform}");
                    // A fix shipped on several lines flips at each line's own patch.
                    foreach (var line in cap.IntroducedInPatchByLine ?? [])
                    {
                        Assert.That(Probe(cmd, fn, line - 1, true), Is.EqualTo(0), $"{fn}() must be 0 just below {line} on {Platform}");
                        Assert.That(Probe(cmd, fn, line, true), Is.EqualTo(1), $"{fn}() must be 1 at {line} on {Platform}");
                    }
                }
            });
        }
        finally
        {
            cmd.CommandText = "SET @schemasmith_version_override = NULL, @schemasmith_version_patch_override = NULL";
            cmd.ExecuteNonQuery();
        }
    }

    private static int Probe(IDbCommand cmd, string fn, int overrideComparable, bool patch)
    {
        cmd.CommandText = patch
            ? $"SET @schemasmith_version_override = {overrideComparable / 100}, @schemasmith_version_patch_override = {overrideComparable}"
            : $"SET @schemasmith_version_override = {overrideComparable}, @schemasmith_version_patch_override = NULL";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"SELECT {fn}()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
