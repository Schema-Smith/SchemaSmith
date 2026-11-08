// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// H3, through a real deploy: a package that stops declaring a table or a column has the deploy drop it, rows and values
/// with it. Target:DestructiveChangePolicy decides what happens: allow and warn drop it (warn says so), fail refuses the step
/// before anything in it is dropped unless Target:AcceptDestructive names the change. The data must be there after a
/// refusal -- that is the outcome these assert, not the message alone.
/// </summary>
[NonParallelizable]
public abstract class DestructiveChangeSharedTests : ObjectScriptDeployFixture
{
    /// <summary>A table named <paramref name="table"/> with an id key and the given extra columns, all int.</summary>
    protected abstract string TableJson(string table, params string[] columns);
    protected abstract string TableFileName(string table);
    /// <summary>schema.table as the destructive-change list names it.</summary>
    protected abstract string Qualified(string table);
    protected abstract string Insert(string table, string column, int value);
    protected abstract string Drop(string table);
    protected abstract bool ColumnExists(string table, string column);
    protected abstract long SumOf(string table, string column);
    protected abstract long RowCount(string table);

    protected const string Kept = "dc_kept", Gone = "dc_gone";

    [SetUp]
    public void TwoTablesWithData()
    {
        Exec(Drop(Kept));
        Exec(Drop(Gone));
        WritePackage("");
        WriteFile("Tables", TableFileName(Kept), TableJson(Kept, "a", "b"));
        WriteFile("Tables", TableFileName(Gone), TableJson(Gone, "a"));
        Deploy(dropRemovedTables: true);
        Exec(Insert(Kept, "b", 7));
        Exec(Insert(Gone, "a", 9));
    }

    [TearDown]
    public void DropThem()
    {
        Exec(Drop(Kept));
        Exec(Drop(Gone));
    }

    protected void StopDeclaringColumnBAndTableGone()
    {
        WritePackage("");
        WriteFile("Tables", TableFileName(Kept), TableJson(Kept, "a"));
    }

    protected static Dictionary<string, string> Policy(string policy, string accept = null)
    {
        var settings = new Dictionary<string, string> { ["Target:DestructiveChangePolicy"] = policy };
        if (accept != null) settings["Target:AcceptDestructive"] = accept;
        return settings;
    }

    [Test]
    public void UnderFail_TheStepIsRefusedAndTheTableAndColumnKeepTheirData()
    {
        StopDeclaringColumnBAndTableGone();
        var result = Deploy(expectSuccess: false, dropRemovedTables: true, settings: Policy("fail"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Failure?.Error, Does.Contain("Destructive change refused").And.Contain(Qualified(Kept) + ".b").And.Contain(Qualified(Gone)));
            Assert.That(ColumnExists(Kept, "b"), Is.True, "the column is still there");
            Assert.That(SumOf(Kept, "b"), Is.EqualTo(7), "with its values");
            Assert.That(RowCount(Gone), Is.EqualTo(1), "the removed table is still there, with its row");
            Assert.That(result.Audit.Where(r => r.Action == "destructive").Select(r => r.Mode).Distinct(), Is.EqualTo(new[] { "refused" }));
        });
    }

    [Test]
    public void UnderFail_AnAcceptedChangeGoesAheadAndAnUnnamedOneIsStillRefused()
    {
        StopDeclaringColumnBAndTableGone();
        var result = Deploy(expectSuccess: false, dropRemovedTables: true, settings: Policy("fail", $"{Qualified(Kept)}.b"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Failure?.Error, Does.Contain(Qualified(Gone)).And.Not.Contain(Qualified(Kept) + ".b"), "the table was not accepted");
            Assert.That(ColumnExists(Kept, "b"), Is.True, "nothing in the refused step was dropped, the accepted column included");
        });

        var accepted = Deploy(dropRemovedTables: true, settings: Policy("fail", $"{Qualified(Kept)}.b; {Qualified(Gone)}"));
        Assert.Multiple(() =>
        {
            Assert.That(ColumnExists(Kept, "b"), Is.False, "the accepted column was dropped");
            Assert.That(accepted.Audit.Where(r => r.Action == "destructive").Select(r => r.Mode).Distinct(), Is.EqualTo(new[] { "accepted" }));
        });
    }

    [Test]
    public void UnderWarn_TheChangeIsMadeAndWarned()
    {
        StopDeclaringColumnBAndTableGone();
        var result = Deploy(dropRemovedTables: true, settings: Policy("warn"));

        Assert.Multiple(() =>
        {
            Assert.That(ColumnExists(Kept, "b"), Is.False);
            Assert.That(result.Log.Count(l => l != null && l.StartsWith("WARN") && l.Contains("Destructive change")), Is.EqualTo(2), string.Join("\n", result.Log.Where(l => l?.Contains("Destructive") == true)));
            Assert.That(result.Audit.Single(r => r.Action == "destructive" && r.ObjectName == Qualified(Kept) + ".b").EstimatedRows, Is.EqualTo(1), "the table's rows, from the catalog");
        });
    }

    [Test]
    public void APreviewUnderFail_ListsTheChangesFailsAndChangesNothing()
    {
        StopDeclaringColumnBAndTableGone();
        var result = Deploy(whatIf: true, expectSuccess: false, dropRemovedTables: true, settings: Policy("fail"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Failure?.Error, Does.Contain("would be refused").And.Contain(Qualified(Kept) + ".b").And.Contain(Qualified(Gone)));
            Assert.That(ColumnExists(Kept, "b"), Is.True);
            Assert.That(RowCount(Gone), Is.EqualTo(1));
        });
    }

    [Test]
    public void ADropTheDeployWillNotMake_IsNotADestructiveChange()
    {
        StopDeclaringColumnBAndTableGone();
        // DropTablesRemovedFromProduct and DropColumnsRemovedFromProduct off: nothing is dropped, so nothing is refused.
        var result = Deploy(dropRemovedTables: false, keepColumns: true, settings: Policy("fail"));

        Assert.Multiple(() =>
        {
            Assert.That(ColumnExists(Kept, "b"), Is.True);
            Assert.That(result.Audit.Where(r => r.Action == "destructive"), Is.Empty);
        });
    }
}
