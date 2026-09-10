// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;

namespace SchemaQuench.UnitTests;

/// <summary>
/// The gate on the scheduled-event step (MySQL/MariaDB, 2.6.0 F4).
///
/// <para>This is a control-flow defect, not a SQL one, which is why it is tested here rather than against
/// a container. <c>DatabaseQuench.QuenchEvents</c> has always handled "declare none" correctly -- it
/// deliberately falls through with a canonical <c>"[]"</c> when by-absence removal is on. The step gate
/// asked only whether any event was declared, so that fall-through was unreachable in the one case it
/// was written for, and removing the LAST declared event left it deployed forever.</para>
///
/// <para>Removing one of TWO declared events always worked. That is what made this look like a working
/// feature for a whole release.</para>
/// </summary>
[TestFixture]
public class DatabaseQuenchEventStepGateTests
{
    // ---- the regression --------------------------------------------------------

    [Test]
    public void ShouldQuenchEvents_NothingDeclaredButDroppingByAbsence_RunsTheStep()
    {
        Assert.That(DatabaseQuench.ShouldQuenchEvents(Platform.MySQL, declaredEventCount: 0, dropRemovedEvents: true),
            Is.True,
            "an empty Events/ folder means 'declare none', not 'skip the comparison' -- this is the case "
            + "that left a live scheduled job running in production after the package that owned it "
            + "said it should be gone");
    }

    [Test]
    public void ShouldQuenchEvents_NothingDeclaredButDroppingByAbsence_RunsTheStepOnMariaDb()
    {
        Assert.That(DatabaseQuench.ShouldQuenchEvents(Platform.MariaDb, declaredEventCount: 0, dropRemovedEvents: true),
            Is.True, "the defect reproduced identically on MySQL 8.0 and MariaDB 11.4");
    }

    // ---- what must NOT change --------------------------------------------------

    [Test]
    public void ShouldQuenchEvents_NothingDeclaredAndNotDropping_SkipsTheStep()
    {
        Assert.That(DatabaseQuench.ShouldQuenchEvents(Platform.MySQL, declaredEventCount: 0, dropRemovedEvents: false),
            Is.False,
            "the overwhelmingly common package declares no events and does not set the flag -- it must "
            + "still pay no round trip. The fix must not turn the empty folder into an unconditional pass");
    }

    [Test]
    public void ShouldQuenchEvents_EventsDeclared_RunsTheStepRegardlessOfTheFlag()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DatabaseQuench.ShouldQuenchEvents(Platform.MySQL, 2, dropRemovedEvents: false), Is.True,
                "declared events always converge -- by-absence removal is a separate question");
            Assert.That(DatabaseQuench.ShouldQuenchEvents(Platform.MySQL, 2, dropRemovedEvents: true), Is.True);
        });
    }

    // ---- engine scoping --------------------------------------------------------

    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    public void ShouldQuenchEvents_OnAnEngineWithoutScheduledEvents_NeverRuns(Platform platform)
    {
        Assert.Multiple(() =>
        {
            Assert.That(DatabaseQuench.ShouldQuenchEvents(platform, 0, dropRemovedEvents: true), Is.False,
                "scheduled events are a MySQL-family feature -- the by-absence flag must not reach "
                + "an engine that has no events to compare");
            Assert.That(DatabaseQuench.ShouldQuenchEvents(platform, 3, dropRemovedEvents: true), Is.False);
        });
    }
}
