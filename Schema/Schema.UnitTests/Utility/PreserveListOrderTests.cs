// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// A re-extract should diff to what actually changed. Sorting the whole file every time buries a
/// one-column change under a whole-file reshuffle, and destroys the ordering of a hand-authored package
/// where the sequence usually carries meaning.
/// </summary>
public class PreserveListOrderTests
{
    private static Table TableWith(params string[] columnNames) =>
        new() { Name = "[T]", Columns = columnNames.Select(n => new Column { Name = n }).ToList() };

    private static IEnumerable<string> Names(Table t) => t.Columns.Select(c => c.Name);

    // Only SQL Server's own lists kept their order, so a PostgreSQL or MySQL-family re-extract reshuffled these every time.
    [Test]
    public void PostgreSqlAndMySqlFamilyLists_KeepTheOrderTheFileHad()
    {
        static List<T> Of<T>(System.Func<string, T> make, params string[] names) => names.Select(make).ToList();
        var pgOriginal = new Schema.Domain.PostgreSQL.PostgreSqlTable
        {
            Name = "t",
            Statistics = Of(n => new Schema.Domain.PostgreSQL.Statistic { Name = n }, "st_z", "st_a"),
            ExcludeConstraints = Of(n => new Schema.Domain.PostgreSQL.ExcludeConstraint { Name = n }, "ex_z", "ex_a"),
            Policies = Of(n => new Schema.Domain.PostgreSQL.PostgreSqlPolicy { Name = n }, "pol_z", "pol_a"),
        };
        var pgExtracted = new Schema.Domain.PostgreSQL.PostgreSqlTable
        {
            Name = "t",
            Statistics = Of(n => new Schema.Domain.PostgreSQL.Statistic { Name = n }, "st_a", "st_z"),
            ExcludeConstraints = Of(n => new Schema.Domain.PostgreSQL.ExcludeConstraint { Name = n }, "ex_a", "ex_z"),
            Policies = Of(n => new Schema.Domain.PostgreSQL.PostgreSqlPolicy { Name = n }, "pol_a", "pol_z"),
        };
        var mariaOriginal = new Schema.Domain.MariaDb.MariaDbTable
        {
            Name = "t",
            FullTextIndexes = Of(n => new Schema.Domain.MySQL.FullTextIndex { Name = n }, "ft_z", "ft_a"),
            Periods = Of(n => new Schema.Domain.MariaDb.TablePeriod { Name = n }, "p_z", "p_a"),
        };
        var mariaExtracted = new Schema.Domain.MariaDb.MariaDbTable
        {
            Name = "t",
            FullTextIndexes = Of(n => new Schema.Domain.MySQL.FullTextIndex { Name = n }, "ft_a", "ft_z"),
            Periods = Of(n => new Schema.Domain.MariaDb.TablePeriod { Name = n }, "p_a", "p_z"),
        };

        ImportTableHelper.PreserveListOrder(pgExtracted, pgOriginal, ObjectOrder.Name);
        ImportTableHelper.PreserveListOrder(mariaExtracted, mariaOriginal, ObjectOrder.Name);

        Assert.Multiple(() =>
        {
            Assert.That(pgExtracted.Statistics.Select(s => s.Name), Is.EqualTo(new[] { "st_z", "st_a" }));
            Assert.That(pgExtracted.ExcludeConstraints.Select(e => e.Name), Is.EqualTo(new[] { "ex_z", "ex_a" }));
            Assert.That(pgExtracted.Policies.Select(p => p.Name), Is.EqualTo(new[] { "pol_z", "pol_a" }));
            Assert.That(mariaExtracted.FullTextIndexes.Select(f => f.Name), Is.EqualTo(new[] { "ft_z", "ft_a" }));
            Assert.That(mariaExtracted.Periods.Select(p => p.Name), Is.EqualTo(new[] { "p_z", "p_a" }));
        });
    }

    [Test]
    public void ExistingEntriesKeepTheOrderTheFileHad()
    {
        var original = TableWith("[Zebra]", "[Apple]", "[Mango]");   // hand-authored, deliberately unsorted
        var extracted = TableWith("[Apple]", "[Mango]", "[Zebra]");  // extraction sorted them

        ImportTableHelper.PreserveListOrder(extracted, original, ObjectOrder.Name);

        Assert.That(Names(extracted), Is.EqualTo(new[] { "[Zebra]", "[Apple]", "[Mango]" }),
            "the author's sequence must survive a re-extract");
    }

    [Test]
    public void NewEntriesAppendInTheFallbackOrder()
    {
        var original = TableWith("[Zebra]", "[Apple]");
        var extracted = TableWith("[Apple]", "[Beta]", "[Alpha]", "[Zebra]");

        ImportTableHelper.PreserveListOrder(extracted, original, ObjectOrder.Name);

        Assert.That(Names(extracted), Is.EqualTo(new[] { "[Zebra]", "[Apple]", "[Alpha]", "[Beta]" }),
            "known entries hold their place; genuinely new ones append, sorted by name");
    }

    [Test]
    public void PhysicalFallbackLeavesNewEntriesAsExtractionProducedThem()
    {
        var original = TableWith("[Zebra]");
        var extracted = TableWith("[Zebra]", "[Beta]", "[Alpha]");

        ImportTableHelper.PreserveListOrder(extracted, original, ObjectOrder.Physical);

        Assert.That(Names(extracted), Is.EqualTo(new[] { "[Zebra]", "[Beta]", "[Alpha]" }),
            "Physical means 'as the table has them', so new entries are not re-sorted");
    }

    [Test]
    public void EntriesRemovedFromTheDatabaseDropOut()
    {
        var original = TableWith("[Zebra]", "[Gone]", "[Apple]");
        var extracted = TableWith("[Apple]", "[Zebra]");

        ImportTableHelper.PreserveListOrder(extracted, original, ObjectOrder.Name);

        Assert.That(Names(extracted), Is.EqualTo(new[] { "[Zebra]", "[Apple]" }),
            "preserving order must not resurrect a column the database no longer has");
    }

    [Test]
    public void QuotingDifferencesStillMatch()
    {
        // A hand-authored file may quote differently from extraction. Treating those as all-new would
        // append everything and reshuffle the whole file -- the exact churn this exists to prevent.
        var original = new Table
        {
            Name = "[T]",
            Columns = [new Column { Name = "Zebra" }, new Column { Name = "\"Apple\"" }]
        };
        var extracted = TableWith("[Apple]", "[Zebra]");

        ImportTableHelper.PreserveListOrder(extracted, original, ObjectOrder.Name);

        Assert.That(Names(extracted), Is.EqualTo(new[] { "[Zebra]", "[Apple]" }));
    }

    [Test]
    public void AFirstExtractIsUntouched()
    {
        var extracted = TableWith("[Apple]", "[Zebra]");
        Assert.DoesNotThrow(() => ImportTableHelper.PreserveListOrder(extracted, null, ObjectOrder.Name));
        Assert.That(Names(extracted), Is.EqualTo(new[] { "[Apple]", "[Zebra]" }),
            "there is nothing to preserve against, so extraction's own order stands");
    }
}
