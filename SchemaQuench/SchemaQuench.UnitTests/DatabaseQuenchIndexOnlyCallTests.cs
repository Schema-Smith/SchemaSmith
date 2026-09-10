// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;

namespace SchemaQuench.UnitTests;

/// <summary>
/// The arguments SchemaQuench actually EMITS for the index-only quench.
///
/// <para><c>IndexOnlyTableQuenches</c> was completely non-functional on PostgreSQL: the generated CALL
/// started at <c>p_TableDefinitions</c> and never passed <c>p_ProductName</c>, which the installed
/// procedure declares FIRST and with no default. No overload matched, so every deploy with the flag on
/// failed at exit 2 -- and PostgreSQL reports that as <c>42883 … procedure does not exist</c>, which
/// sends the user hunting for a broken installation when the procedure is installed and correct.</para>
///
/// <para><b>Why the existing coverage missed it, which is the part worth not repeating.</b> The
/// integration base class hand-writes its own CALL and has always passed <c>p_ProductName</c>, so it
/// exercised a string SchemaQuench never emitted. And the nearest unit test asserts only that the
/// escaped product name appears SOMEWHERE in the emitted batch -- true even with the argument missing,
/// because the <c>FixupIndexOwnership</c> call two lines later carries it. These tests assert against
/// the <c>IndexOnlyQuench</c> statement specifically.</para>
/// </summary>
[TestFixture]
public class DatabaseQuenchIndexOnlyCallTests
{
    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    [Test]
    public void QuenchIndexesAndConstraints_PostgreSqlIndexOnly_PassesProductNameToIndexOnlyQuench()
    {
        var emitted = EmitIndexOnlyBatch(Platform.PostgreSQL, "VendorIndexes");

        Assert.That(IndexOnlyStatement(emitted), Does.Contain("p_ProductName"),
            "the installed procedure declares p_ProductName first and with NO default, so omitting it "
            + "means no overload matches and PostgreSQL reports 42883 -- a documented four-engine "
            + "feature that does not work at all on one of them");
    }

    [Test]
    public void QuenchIndexesAndConstraints_PostgreSqlIndexOnly_PassesTheActualProductName()
    {
        var emitted = EmitIndexOnlyBatch(Platform.PostgreSQL, "VendorIndexes");

        Assert.That(IndexOnlyStatement(emitted), Does.Contain("p_ProductName := 'VendorIndexes'"),
            "p_ProductName is load-bearing, not decorative -- IndexOnlyQuench.sql uses it at :199 and "
            + ":232 to scope index ownership, so passing the wrong value silently mis-scopes ownership "
            + "rather than failing loudly");
    }

    [Test]
    public void QuenchIndexesAndConstraints_PostgreSqlIndexOnly_EscapesAnApostropheInTheProductName()
    {
        var emitted = EmitIndexOnlyBatch(Platform.PostgreSQL, "O'Brien's DB");

        Assert.That(IndexOnlyStatement(emitted), Does.Contain("p_ProductName := 'O''Brien''s DB'"),
            "and it must be escaped in this statement specifically -- asserting the escaped name appears "
            + "somewhere in the batch passes even when this argument is missing entirely, because "
            + "FixupIndexOwnership carries it too");
    }

    [Test]
    public void QuenchIndexesAndConstraints_SqlServerIndexOnly_StillPassesProductName()
    {
        var emitted = EmitIndexOnlyBatch(Platform.SqlServer, "VendorIndexes");

        Assert.That(emitted, Does.Contain("@ProductName = 'VendorIndexes'"),
            "SQL Server was always correct -- this is the parity anchor that says the PostgreSQL fix "
            + "brought it into line rather than inventing something new");
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>
    /// Drives the real emission path and returns the whole command text. Nothing executes -- the command
    /// is a substitute, so this asserts on what SchemaQuench WOULD send.
    /// </summary>
    private static string EmitIndexOnlyBatch(Platform platform, string productName)
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = productName, Platform = platform };
        var template = new Template { Name = "T", IndexOnlyTableQuenches = true };
        var flag = platform == Platform.PostgreSQL ? "false" : "0";

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, flag, false, flag, flag, flag, flag, flag, flag, flag, flag, false, false, null);

        var mockCmd = Substitute.For<IDbCommand>();
        var mockConnection = Substitute.For<IDbConnection>();
        mockConnection.Database.Returns("testdb");
        mockCmd.Connection.Returns(mockConnection);

        quench.QuenchIndexesAndConstraints(mockCmd);
        return mockCmd.CommandText;
    }

    /// <summary>
    /// The IndexOnlyQuench statement alone. The PostgreSQL branch emits a three-statement batch, and two
    /// of the other statements legitimately carry the product name -- so a whole-batch assertion cannot
    /// tell a passed argument from a neighbouring one.
    /// </summary>
    private static string IndexOnlyStatement(string batch)
        => batch.Split(';').FirstOrDefault(s => s.Contains("IndexOnlyQuench")) ?? "";

    // Mirrors the helper in DatabaseQuenchTests: LogSqlScript reaches IFile/IDirectory and resolves the
    // artifact directory through IConfigurationRoot, so all three need a stub or FactoryContainer throws.
    private static void RegisterMockFileWrapper()
    {
        FactoryContainer.Register<IFile>(Substitute.For<IFile>());
        FactoryContainer.Register<IDirectory>(Substitute.For<IDirectory>());
        if (FactoryContainer.Resolve<IConfigurationRoot>() == null)
            FactoryContainer.Register<IConfigurationRoot>(Substitute.For<IConfigurationRoot>());
    }
}
