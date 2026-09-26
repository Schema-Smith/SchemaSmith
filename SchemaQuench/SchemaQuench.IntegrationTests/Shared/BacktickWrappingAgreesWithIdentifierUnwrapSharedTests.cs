// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// The deployed <c>SchemaSmith_StripBacktickWrapping</c> and the C# <c>Identifier.Unwrap</c> must return the
/// same answer for the same identifier — the MySQL-family twin of the SQL Server agreement test.
/// <para>They are the two halves of one decision, what a stored name means, and the deploy unwraps the
/// package side in C# and the catalog side in SQL. A disagreement is a rename that matches nothing: the new
/// object is created, the old one survives undeclared, and a later deploy carrying the matching
/// <c>Drop…RemovedFromProduct</c> flag removes the orphan WITH ITS ROWS.</para>
/// <para>The C# half is already right — <c>MySqlReservedWords.Unquote</c> strips ONE pair and then collapses
/// a doubled backtick, the same shape as SQL Server's. The SQL half used
/// <c>TRIM(BOTH '`' FROM identifier)</c>, which strips EVERY leading and trailing backtick rather than one
/// pair, so it diverges on a name that begins or ends with one.</para>
/// <para>Asserted as AGREEMENT rather than against literals, for the same reason as the SQL Server twin: the
/// property that must hold is that the two sides cannot drift apart, and hard-coded expectations would let a
/// future divergence be "fixed" by editing this file to match whichever side someone had just changed.</para>
/// </summary>
public abstract class BacktickWrappingAgreesWithIdentifierUnwrapSharedTests
{
    protected abstract Platform Platform { get; }
    /// <summary>A connection string for a database the forge has been kindled into: the
    /// <c>SchemaSmith_*</c> helpers are created PER DATABASE, so information_schema has none of them
    /// and every case fails identically for a structural reason rather than a divergence.</summary>
    protected abstract string KindledDbConnectionString { get; }

    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform)
            .GetDbConnection(KindledDbConnectionString);
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    /// <summary>
    /// Every shape a stored MySQL-family identifier can take, with the diverging ones named so a failure
    /// says which case broke.
    /// </summary>
    private static IEnumerable<TestCaseData> Identifiers()
    {
        yield return new TestCaseData("`Orders`").SetName("ordinary delimited name");
        yield return new TestCaseData("Orders").SetName("already bare");
        yield return new TestCaseData("`Order Details`").SetName("delimited name with a space");
        yield return new TestCaseData("`a``b`").SetName("delimited name containing an escaped backtick");
        yield return new TestCaseData("``").SetName("delimited empty name");
        // THE ONE INPUT ON WHICH THE TWO HALVES ACTUALLY DIVERGED, and the original eight cases all missed
        // it: a LONE delimiter. C# Unquote threw ArgumentOutOfRangeException (it both starts and ends with a
        // backtick, so Substring(1, -1)) while the deployed function guarded on length and returned the
        // input. An agreement gate that omits the disagreeing input is not a gate.
        yield return new TestCaseData("`").SetName("lone delimiter");
        // TRIM(BOTH) eats every backtick at each end, so these are where the two halves part company.
        yield return new TestCaseData("`a``").SetName("DIVERGED: name ending in a backtick");
        yield return new TestCaseData("```a`").SetName("DIVERGED: name starting with a backtick");
        yield return new TestCaseData("```a```").SetName("DIVERGED: name starting AND ending with one");
    }

    [TestCaseSource(nameof(Identifiers))]
    public void TheDeployedFunctionAndTheHelperUnwrapIdentically(string stored)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT SchemaSmith_StripBacktickWrapping(@p)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p";
        p.Value = stored;
        cmd.Parameters.Add(p);

        var fromSql = cmd.ExecuteScalar() as string;
        var fromHelper = Identifier.Unwrap(stored, Platform.MySQL);

        Assert.That(fromSql, Is.EqualTo(fromHelper),
            $"SchemaSmith_StripBacktickWrapping and Identifier.Unwrap disagree on '{stored}'. The deploy "
            + "unwraps the package side in C# and the catalog side in SQL, so a disagreement means a rename "
            + "that matches nothing: the new object is created, the old one is left undeclared, and a later "
            + "deploy with a Drop...RemovedFromProduct flag removes it with its rows.");
    }
}
