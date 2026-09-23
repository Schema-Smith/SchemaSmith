// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Behavioural cover for <c>SchemaSmith.PrintWithNoWait</c>, the procedure every generated statement is
/// printed through — and therefore the whole of what a user sees from <c>--WhatIf</c>.
/// <para>
/// It had none. The v2.7.0 rewrite replaced a SUBSTRING-consuming loop (quadratic: 53,846 ms to print
/// one statement's output on a 1,783-table model) with an advancing position, and nothing in the suite
/// asserted a single line of its output. That is not a pure performance change: it moved the
/// line-ending handling to "cut at the line feed and drop a carriage return in front of it" and the
/// end-of-message test from LEN to DATALENGTH, each of which decides whether a line survives. A
/// regression here drops or mangles the preview and no assertion notices.
/// </para>
/// <para>
/// The trailing-spaces case is not hypothetical: the final line was still gated on <c>LEN</c>, which
/// reports 0 for a line made entirely of spaces, so a closing line of indentation was silently dropped.
/// </para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class PrintWithNoWaitTests
{
    private static List<string> Print(string message)
    {
        var lines = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        conn.ChangeDatabase(FixtureSetup.MainDb);

        // Subscribed AFTER ChangeDatabase on purpose. It raises its own "Changed database context to ..."
        // info message, but only on a physical connection -- a pooled one already on that database says
        // nothing -- so capturing it would make every assertion here depend on whether the pool happened
        // to hand back a warm connection.
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors) lines.Add(err.Message);
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC [SchemaSmith].[PrintWithNoWait] @Message";
        var p = cmd.CreateParameter();
        p.ParameterName = "@Message";
        p.Value = message;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();

        return lines;
    }

    [Test]
    public void UnixLineEndings_EachLineIsPrintedOnce_AndTheLastOneSurvives()
    {
        // The last line carries no terminator, which is the branch that used to be reached by consuming
        // the string down to nothing -- and is now reached by the position passing the end.
        Assert.That(Print("alpha\nbeta\ngamma"), Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
    }

    [Test]
    public void WindowsLineEndings_TheCarriageReturnIsNotPrintedAsPartOfTheLine()
    {
        var lines = Print("alpha\r\nbeta\r\ngamma");

        Assert.That(lines, Is.EqualTo(new[] { "alpha", "beta", "gamma" }),
            "a CR left on the end of each line would still LOOK right in a console and would corrupt "
            + "any consumer that compares the text");
    }

    [Test]
    public void ATrailingNewlineDoesNotProduceAnExtraEmptyLine()
    {
        Assert.That(Print("alpha\nbeta\n"), Is.EqualTo(new[] { "alpha", "beta" }));
    }

    [Test]
    public void AnInteriorBlankLineIsPreserved()
    {
        // Generated DDL is separated by blank lines; collapsing them would run statements together.
        // The blank arrives as a single space rather than an empty string -- that is RAISERROR's own
        // rendering of an empty message, not something this procedure chooses, and it predates the
        // rewrite. What matters is that the line is still THERE, in position.
        Assert.That(Print("alpha\n\nbeta"), Is.EqualTo(new[] { "alpha", " ", "beta" }));
    }

    [Test]
    public void AFinalLineOfOnlySpacesIsStillPrinted()
    {
        // The regression this file was written for. LEN() reports 0 for "   ", so the final line was
        // dropped; DATALENGTH does not, and the line is content like any other.
        var lines = Print("alpha\n   ");

        Assert.That(lines, Has.Count.EqualTo(2),
            "a final line made only of spaces was silently dropped when the guard used LEN");
        Assert.That(lines[0], Is.EqualTo("alpha"));
    }

    [Test]
    public void ALineEndingInSpacesKeepsThem()
    {
        var lines = Print("alpha   \nbeta");

        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0], Does.StartWith("alpha"));
        Assert.That(lines[1], Is.EqualTo("beta"),
            "the line after one that ends in spaces must not be absorbed into it");
    }

    [Test]
    public void AnEmptyMessagePrintsNothingRatherThanThrowing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Print(""), Is.Empty, "an empty message has no lines at all");
            // A message that is ONLY a terminator is one empty line, not zero -- and RAISERROR renders
            // an empty line as a single space (see AnInteriorBlankLineIsPreserved).
            Assert.That(Print("\n"), Is.EqualTo(new[] { " " }));
        });
    }

    [Test]
    public void ALongMessageIsPrintedWholeAndInOrder()
    {
        // The rewrite's whole point is that the message is read once end to end rather than rebuilt per
        // line. A position that fails to advance correctly at scale shows up here and nowhere smaller.
        var expected = new List<string>();
        var message = new System.Text.StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            var line = $"line {i:D4} " + new string('x', 40);
            expected.Add(line);
            message.Append(line).Append('\n');
        }

        Assert.That(Print(message.ToString()), Is.EqualTo(expected));
    }
}
