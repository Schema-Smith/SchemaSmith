// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

[TestFixture]
public class SequenceRangeCheckTests
{
    private static ValidationContext Context(Platform platform, System.Action<Template> add)
    {
        var template = new Template { Name = "Main" };
        add(template);
        return new ValidationContext(new Product { Name = "P", Platform = platform }, [template], "pkg");
    }

    private static string[] Messages(ValidationContext ctx) =>
        new SequenceRangeCheck().Run(ctx).Where(f => f.Code == "SS-SEQ-003" && f.Severity == Severity.Error).Select(f => f.Message).ToArray();

    [Test]
    public void AZeroIncrement_IsAnError_OnEveryEngine()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Messages(Context(Platform.SqlServer, t => t.SqlServerSequences.Add(new SqlServerSequence { Name = "S", Increment = 0 }))), Has.Some.Contains("Increment is 0"));
            Assert.That(Messages(Context(Platform.PostgreSQL, t => t.Sequences.Add(new PostgreSqlSequence { Name = "S", Increment = 0 }))), Has.Some.Contains("Increment is 0"));
            Assert.That(Messages(Context(Platform.MariaDb, t => t.MariaDbSequences.Add(new MariaDbSequence { Name = "S", Increment = 0 }))), Has.Some.Contains("Increment is 0"));
        });
    }

    [Test]
    public void AMinimumNotBelowTheMaximum_IsAnError()
    {
        var messages = Messages(Context(Platform.SqlServer, t => t.SqlServerSequences.Add(new SqlServerSequence { Name = "S", MinValue = 10, MaxValue = 10 })));
        Assert.That(messages, Has.Some.Contains("MinValue (10) is not below MaxValue (10)"));
    }

    [Test]
    public void AStartOutsideTheRange_IsAnError()
    {
        var messages = Messages(Context(Platform.PostgreSQL, t => t.Sequences.Add(new PostgreSqlSequence { Name = "S", Start = 0, MinValue = 1 })));
        Assert.That(messages, Has.Some.Contains("Start (0) is outside the declared range"));
    }

    [Test]
    public void ANegativeCache_IsAnError()
    {
        var messages = Messages(Context(Platform.SqlServer, t => t.SqlServerSequences.Add(new SqlServerSequence { Name = "S", Cache = -1 })));
        Assert.That(messages, Has.Some.Contains("Cache is -1"));
    }

    [Test]
    public void ASensibleSequence_HasNoFinding()
    {
        var ctx = Context(Platform.SqlServer, t => t.SqlServerSequences.Add(new SqlServerSequence { Name = "S", Start = 5, Increment = -1, MinValue = 0, MaxValue = 5, Cache = 0 }));
        Assert.That(new SequenceRangeCheck().Run(ctx), Is.Empty);
    }
}
