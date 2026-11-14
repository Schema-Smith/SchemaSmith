// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// The four outcomes for a read-only extraction target, tested where the decision lives rather than
/// through a database and a log appender.
/// <para>The split that matters is <b>NotKindled versus everything else</b>. Missing helpers is a hard
/// error because there is nothing to extract with, and proceeding would surface later as a confusing
/// "could not find stored procedure" — or produce nothing at all. Stale helpers only warn, because
/// refusing would make an Availability Group readable secondary useless the moment the primary is one
/// version ahead, which is most of the time.</para>
/// </summary>
[TestFixture]
public class ReadOnlyKindleClassificationTests
{
    private const string Expected = "aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbccccccccccccccccdddddddddddddddd";

    [Test]
    public void NoStampStore_IsNotKindled_AndTheStampIsNeverRead()
    {
        var read = false;

        var state = ForgeKindler.ClassifyReadOnlyKindle(storeExists: false,
            readStamp: () => { read = true; return Expected; }, expectedStamp: Expected);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(ForgeKindler.ReadOnlyKindleState.NotKindled));
            Assert.That(read, Is.False,
                "reading the stamp requires the store the caller has just been told does not exist -- on "
                + "MySQL and PostgreSQL that reference is validated at parse time and raises rather than "
                + "returning null, which is why the read is deferred");
        });
    }

    [Test]
    public void AStoreWithNoReadableStamp_IsUnverifiable()
    {
        // Kindled at some point -- the store is there -- but nothing says which version. That is not the
        // same as being current, and reporting it as current would be a lie the user cannot check.
        Assert.That(ForgeKindler.ClassifyReadOnlyKindle(true, () => null, Expected),
            Is.EqualTo(ForgeKindler.ReadOnlyKindleState.Unverifiable));

        Assert.That(ForgeKindler.ClassifyReadOnlyKindle(true, () => "", Expected),
            Is.EqualTo(ForgeKindler.ReadOnlyKindleState.Unverifiable),
            "an empty stamp says as little as a missing one");
    }

    [Test]
    public void ADifferentStamp_IsStale()
    {
        Assert.That(ForgeKindler.ClassifyReadOnlyKindle(true, () => "some-older-build", Expected),
            Is.EqualTo(ForgeKindler.ReadOnlyKindleState.Stale));
    }

    [Test]
    public void AMatchingStamp_IsCurrent()
    {
        Assert.That(ForgeKindler.ClassifyReadOnlyKindle(true, () => Expected, Expected),
            Is.EqualTo(ForgeKindler.ReadOnlyKindleState.Current));
    }

    [Test]
    public void TheComparisonIsExact_NotCaseInsensitiveOrTrimmed()
    {
        // The stamp is a hash. Anything that is not byte-identical is a different build, and treating a
        // near-match as current would silently drop the one signal this whole check exists to give.
        Assert.Multiple(() =>
        {
            Assert.That(ForgeKindler.ClassifyReadOnlyKindle(true, () => Expected.ToUpperInvariant(), Expected),
                Is.EqualTo(ForgeKindler.ReadOnlyKindleState.Stale), "case must not be folded");
            Assert.That(ForgeKindler.ClassifyReadOnlyKindle(true, () => " " + Expected, Expected),
                Is.EqualTo(ForgeKindler.ReadOnlyKindleState.Stale), "whitespace must not be trimmed away");
        });
    }

    // H1: unlike a read-only extraction, a drift check refuses helpers that are missing or out of date -- an answer from
    // mismatched helpers is worse than none -- and only warns when their version cannot be read.
    [Test]
    public void ForDrift_MissingHelpers_StopTheTargetSayingHowToGetThem()
    {
        var ex = Assert.Throws<RunFailedException>(() => ForgeKindler.DriftKindleCheck(ForgeKindler.ReadOnlyKindleState.NotKindled, null, Expected));
        Assert.That(ex!.Message, Does.Contain("KindleTheForge false").And.Contain("Deploy once"));
    }

    [Test]
    public void ForDrift_StaleHelpers_StopTheTargetNamingBothVersions()
    {
        var ex = Assert.Throws<RunFailedException>(() => ForgeKindler.DriftKindleCheck(ForgeKindler.ReadOnlyKindleState.Stale, "0123456789abcdef", Expected));
        Assert.That(ex!.Message, Does.Contain("out of date").And.Contain("0123456789ab").And.Contain(Expected[..12]));
    }

    [Test]
    public void ForDrift_CurrentOrUnverifiableHelpers_GoAhead()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ForgeKindler.DriftKindleCheck(ForgeKindler.ReadOnlyKindleState.Current, Expected, Expected), Does.Contain("current"));
            Assert.That(ForgeKindler.DriftKindleCheck(ForgeKindler.ReadOnlyKindleState.Unverifiable, null, Expected), Does.Contain("cannot be read"));
        });
    }
}
