// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// SchemaTongs overwrites a package in place, so anything extraction cannot reconstruct has to be carried
/// forward from the file being replaced. That carry-forward used to be a hand-written list, and eight
/// deploy-behaviour properties were missing from it — <c>UpdateFillFactor</c>, <c>SampleSize</c>, and the
/// whole <c>Drop*RemovedFromProduct</c> family — so authoring one and re-extracting silently reverted it,
/// leaving a package that is still perfectly valid and no longer does what its author asked.
///
/// These tests make the marking load-bearing: a table/component property must either be something an
/// extractor can produce, or be marked <c>AuthoredOnly</c> (which is what makes it copied). Adding a new
/// property that is neither fails the build here rather than going quiet until someone notices a deploy
/// behaving differently.
/// </summary>
public class AuthoredOnlyPropertyTests
{
    // Table/component shapes only. Product/Template/TemplateTarget are written by a different path with a
    // different preservation story, and sweeping them in here would assert something this helper never did.
    private static IEnumerable<Type> DomainShapes() =>
        typeof(Table).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && t.Namespace != null
                        && t.Namespace.StartsWith("Schema.Domain", StringComparison.Ordinal)
                        && (typeof(Table).IsAssignableFrom(t) || typeof(DynamicBase).IsAssignableFrom(t)));

    [Test]
    public void EveryAuthoredOnlyPropertyIsCopiedForward()
    {
        // The marking is only worth anything if the copier honours it. Build a pair of objects, set the
        // marked properties on one, and require them to arrive on the other.
        var missed = new List<string>();

        foreach (var type in DomainShapes())
        {
            var marked = type.GetProperties()
                .Where(p => p.GetCustomAttribute<SchemaPropertyAttribute>() is { AuthoredOnly: true } && p.CanWrite)
                .ToList();
            if (marked.Count == 0) continue;

            object original, extracted;
            try
            {
                original = Activator.CreateInstance(type);
                extracted = Activator.CreateInstance(type);
            }
            catch (MissingMethodException) { continue; } // no parameterless ctor — not a package shape
            if (original == null || extracted == null) continue;

            foreach (var p in marked)
            {
                var sample = SampleValueFor(p.PropertyType);
                // A null sample cannot distinguish "copied" from "never set" -- both sides read null and
                // the comparison below passes without testing anything. Fail here instead, so a future
                // AuthoredOnly property of an un-constructible type is a red build rather than a silent
                // hole in this guard.
                Assert.That(sample, Is.Not.Null,
                    $"{type.Name}.{p.Name} is AuthoredOnly but SampleValueFor cannot produce a value for "
                    + $"{p.PropertyType.Name}, which would make this check vacuous for it. Teach "
                    + "SampleValueFor about the type.");
                p.SetValue(original, sample);
            }

            // Drive the PUBLIC entry point SchemaTongs actually calls, not the copier directly.
            // Calling the copier straight would still pass with the call site removed -- which is
            // precisely what the first version of this test did (caught by mutation).
            if (original is Table origTable && extracted is Table newTable)
                ImportTableHelper.PreserveDataDeliveryAndCustomProperties(newTable, origTable, _ => true);
            else
                ImportTableHelper.CopyAuthoredOnlyProperties(original, extracted);

            missed.AddRange(from p in marked
                            let want = p.GetValue(original)
                            let got = p.GetValue(extracted)
                            where !Equals(want, got)
                            select $"{type.Name}.{p.Name} (expected {want}, got {got})");
        }

        Assert.That(missed, Is.Empty,
            "these properties are marked AuthoredOnly but do not survive a re-extract:\r\n  "
            + string.Join("\r\n  ", missed));
    }

    [Test]
    public void TheEightKnownLossesAreMarked()
    {
        // Pins the specific properties the 2026-08-25 audit found silently reverting. A regression here
        // means one was unmarked — and unmarking is exactly how they were lost the first time.
        var expected = new[]
        {
            "UpdateFillFactor", "SampleSize",
            "DropColumnsRemovedFromProduct", "DropIndexesRemovedFromProduct",
            "DropForeignKeysRemovedFromProduct", "DropCheckConstraintsRemovedFromProduct",
            "DropStatisticsRemovedFromProduct", "DropExcludeConstraintsRemovedFromProduct"
        };

        var marked = DomainShapes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.GetCustomAttribute<SchemaPropertyAttribute>() is { AuthoredOnly: true })
            .Select(p => p.Name)
            .Distinct()
            .ToHashSet();

        Assert.That(expected.Where(e => !marked.Contains(e)), Is.Empty,
            "an audited authoring-only property lost its AuthoredOnly marking, so it will silently revert "
            + "on the next re-extract");
    }

    [Test]
    public void AnUnreadableOriginalDoesNotCrashPreservation()
    {
        // SchemaTongs warns and passes null when the file it is replacing will not parse. Preservation has
        // nothing to carry forward at that point, but it must not take the extraction down with it — the
        // extracted table is already complete and is about to overwrite the broken file.
        var extracted = new Table { Name = "[T]" };
        Assert.DoesNotThrow(() =>
            ImportTableHelper.PreserveDataDeliveryAndCustomProperties(extracted, null, _ => true));
        Assert.That(extracted.Name, Is.EqualTo("[T]"), "the extracted table must be left intact");
    }

    /// <summary>
    /// Produces a value distinguishable from the extracted object's default, so that "did this survive
    /// the copy?" is a question with a real answer.
    /// <para>
    /// This used to return null for any reference type other than string, which made the whole check
    /// VACUOUS for such a property: the sample was null, the extracted value was null, and
    /// <c>Equals(null, null)</c> passed while proving nothing. <c>Table.RebuildPolicy</c> is the first
    /// reference-typed member of the AuthoredOnly family, so it was the first property the guard did
    /// not actually guard. Anything not constructible here now returns null on purpose and the caller
    /// fails on it, rather than passing quietly.
    /// </para>
    /// </summary>
    private static object SampleValueFor(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(bool)) return true;
        if (t == typeof(byte)) return (byte)42;
        if (t == typeof(int)) return 42;
        if (t == typeof(string)) return "authored";
        if (t.IsValueType) return Activator.CreateInstance(t);
        return t.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(t) : null;
    }
}
