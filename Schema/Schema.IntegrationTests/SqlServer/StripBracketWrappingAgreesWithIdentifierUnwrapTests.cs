// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// The deployed <c>SchemaSmith.fn_StripBracketWrapping</c> and the C# <c>Identifier.Unwrap</c> must return
/// the same answer for the same identifier. They are the two halves of one decision — what a stored name
/// means — and the deploy compares a package name unwrapped by the C# side against a catalog name unwrapped
/// by the SQL side, so a disagreement is a rename that matches nothing.
/// <para>Why that matters beyond tidiness: an unmatched rename creates the new object, leaves the old one
/// undeclared, and a later deploy carrying the matching <c>Drop…RemovedFromProduct</c> flag removes the
/// orphan WITH ITS ROWS. That is the same composition the `]]`-collapse fix addressed; this is the
/// remaining half of the disagreement.</para>
/// <para>The function used a <c>WHILE</c> where the helper uses a single conditional strip, so it stripped
/// REPEATEDLY. For every ordinary name that is invisible — <c>[Orders]</c> and <c>Orders</c> unwrap
/// identically either way — and it diverges on exactly one shape: a name that legitimately CONTAINS
/// brackets. Measured on SQL Server 2022 before the change: <c>[[x]]]</c>, the correct delimited form of
/// the name <c>[x]</c>, came back as <c>x</c> instead of <c>[x]</c>.</para>
/// <para>Asserted as AGREEMENT rather than against hard-coded strings on purpose. The property that has to
/// hold is that the two sides cannot drift apart, and pinning expected literals here would let someone
/// "fix" a future divergence by editing this file to match whichever side they happened to change.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class StripBracketWrappingAgreesWithIdentifierUnwrapTests
{
    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    /// <summary>
    /// Every shape a stored SQL Server identifier can actually take, with the ones that used to diverge
    /// named so a failure says which case broke.
    /// </summary>
    private static IEnumerable<TestCaseData> Identifiers()
    {
        yield return new TestCaseData("[Orders]").SetName("ordinary delimited name");
        yield return new TestCaseData("Orders").SetName("already bare");
        yield return new TestCaseData("[Order Details]").SetName("delimited name with a space");
        yield return new TestCaseData("[a]]b]").SetName("delimited name containing an escaped ]");
        yield return new TestCaseData("[]").SetName("delimited empty name");
        yield return new TestCaseData("[[x]]]").SetName("DIVERGED: delimited form of the name [x]");
        yield return new TestCaseData("[[Orders]]]").SetName("DIVERGED: delimited form of the name [Orders]");
        yield return new TestCaseData("[[a]]b]]]").SetName("DIVERGED: brackets AND an escaped ]");
    }

    [TestCaseSource(nameof(Identifiers))]
    public void TheDeployedFunctionAndTheHelperUnwrapIdentically(string stored)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT SchemaSmith.fn_StripBracketWrapping(@p)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p";
        p.Value = stored;
        cmd.Parameters.Add(p);

        var fromSql = (string)cmd.ExecuteScalar()!;
        var fromHelper = Identifier.Unwrap(stored, Platform.SqlServer);

        Assert.That(fromSql, Is.EqualTo(fromHelper),
            $"fn_StripBracketWrapping and Identifier.Unwrap disagree on '{stored}'. The deploy unwraps the "
            + "package side in C# and the catalog side in SQL, so a disagreement means a rename that matches "
            + "nothing: the new object is created, the old one is left undeclared, and a later deploy with a "
            + "Drop...RemovedFromProduct flag removes it with its rows.");
    }
}
