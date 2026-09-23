// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Text;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// <c>IGNORE_DUP_KEY</c> and <c>PAD_INDEX</c> — two of the four gaps the 2026-09-01 scope-boundary
/// derivation confirmed.
/// <para><b>IGNORE_DUP_KEY is not a tuning knob.</b> Verified against SQL Server 2022 before this was
/// built: with it ON a multi-row <c>INSERT</c> containing a duplicate <b>succeeds</b>, the duplicate is
/// discarded and the other rows land; with it OFF the whole statement <b>fails with 2601</b> and nothing
/// lands. Two databases whose index definitions otherwise match disagree about whether an application's
/// INSERT works — that is schema, not performance, which is why it is in scope while
/// <c>ALLOW_ROW_LOCKS</c> and friends are declined.</para>
/// <para><b>PAD_INDEX</b> applies <c>FILLFACTOR</c> to intermediate pages. It is storage format, and
/// <c>FillFactor</c> is already supported, so supporting one and not the other was an accident of what got
/// built rather than a decision.</para>
/// <para><b>The idempotence test is the important one.</b> An index option has to be handled in ten
/// places — the JSON and XML ingest tiers, the declared-index emit, the existing-index reconstruction that
/// drives change detection, and extraction. Miss any one and the reconstructed script never matches the
/// declared one, so every deploy drops and recreates the index forever. That is exactly how the
/// ROWGUIDCOL drift bug behaved.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class IndexOptionTests
{
    private IDbConnection _connection;

    // Uses the suite's already-kindled database rather than creating and kindling its own. A private
    // database costs CREATE DATABASE, ~40 kindling scripts, and a from-scratch plan compile for
    // ModifiedTableQuench (plans cache per object PER DATABASE, so a fresh one never reuses them) --
    // ~2.6s of compile for ~117ms of real work. Nothing here needs a private database: no compatibility
    // level, filegroup, CDC or change-tracking configuration, which is what genuinely forces a fixture to
    // own one. Isolation comes from this fixture's own object names, dropped at both ends so a shared
    // database neither inherits state nor leaks it.
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        DropOwnObjects();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            DropOwnObjects();
        }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
    }

    // Everything this fixture creates, by its own names -- so sharing the suite database cannot leak
    // state into a sibling fixture or inherit it from one.
    private void DropOwnObjects()
    {
        Exec(@"IF OBJECT_ID('dbo.IdxIdem', 'U') IS NOT NULL DROP TABLE dbo.IdxIdem;
               IF OBJECT_ID('dbo.IdkBehave', 'U') IS NOT NULL DROP TABLE dbo.IdkBehave;
               DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'IdxOptTest';");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private int Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
    }

    private void Deploy(string json)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'IdxOptTest', "
                          + $"@TableDefinitions = N'{json.Replace("'", "''")}', "
                          + "@DropTablesRemovedFromProduct = 0";
        cmd.ExecuteNonQuery();
    }

    private static string Package(string table, string options) =>
        "[{ \"Schema\": \"[dbo]\", \"Name\": \"[" + table + "]\", \"Columns\": ["
        + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"[V]\", \"DataType\": \"INT\", \"Nullable\": true } ], \"Indexes\": ["
        + " { \"Name\": \"[PK_" + table + "]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true },"
        + " { \"Name\": \"[UX_" + table + "]\", \"IndexColumns\": \"[V]\", \"Unique\": true" + options + " } ] }]";

    private int IndexFlag(string table, string column) =>
        Scalar($"SELECT CONVERT(INT, i.{column}) FROM sys.indexes i "
               + $"WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.name = 'UX_{table}'");

    [Test]
    public void IgnoreDuplicateKey_IsDeployed()
    {
        Deploy(Package("IdkOn", ", \"IgnoreDuplicateKey\": true"));

        Assert.That(IndexFlag("IdkOn", "ignore_dup_key"), Is.EqualTo(1),
            "deploying it without the option is a green run that silently changes whether a duplicate "
            + "INSERT fails -- the behaviour this exists to declare");
    }

    [Test]
    public void IgnoreDuplicateKey_ChangesWhatAnInsertDoes()
    {
        // Asserting the catalog flag alone would pass over an emit that set the bit without the engine
        // honouring it. This asserts the outcome a user actually sees.
        Deploy(Package("IdkBehave", ", \"IgnoreDuplicateKey\": true"));
        Exec("INSERT dbo.IdkBehave (Id, V) VALUES (1, 10)");

        Assert.DoesNotThrow(() => Exec("INSERT dbo.IdkBehave (Id, V) VALUES (2, 10), (3, 30)"),
            "with IGNORE_DUP_KEY on, a multi-row insert containing a duplicate must succeed");

        Assert.That(Scalar("SELECT COUNT(*) FROM dbo.IdkBehave"), Is.EqualTo(2),
            "the duplicate is discarded and the non-duplicate row lands -- 2 rows, not 1 and not 3");
    }

    [Test]
    public void NotDeclaringIgnoreDuplicateKey_LeavesItOff()
    {
        // The negative half: an emit that added the option unconditionally would pass the assertions
        // above while silently turning every unique index in every package into one that swallows
        // duplicate rows instead of rejecting them.
        Deploy(Package("IdkOff", ""));

        Assert.That(IndexFlag("IdkOff", "ignore_dup_key"), Is.Zero);
    }

    [Test]
    public void PadIndex_IsDeployed()
    {
        Deploy(Package("PadOn", ", \"PadIndex\": true, \"FillFactor\": 70"));

        Assert.That(IndexFlag("PadOn", "is_padded"), Is.EqualTo(1));
    }

    [Test]
    public void NotDeclaringPadIndex_LeavesItOff()
    {
        Deploy(Package("PadOff", ", \"FillFactor\": 70"));

        Assert.That(IndexFlag("PadOff", "is_padded"), Is.Zero);
    }

    [Test]
    public void TheOptions_AreIdempotent()
    {
        // THE test for this feature. An index option lives in ten places -- both ingest tiers, the
        // declared emit, the existing-index reconstruction that drives change detection, and extraction.
        // Miss one and the reconstructed script never equals the declared one, so every deploy drops and
        // recreates the index forever. Nothing errors; it just churns, exactly like the ROWGUIDCOL bug.
        Deploy(Package("IdxIdem", ", \"IgnoreDuplicateKey\": true, \"PadIndex\": true, \"FillFactor\": 70"));
        var firstId = Scalar("SELECT i.index_id FROM sys.indexes i "
                             + "WHERE i.[object_id] = OBJECT_ID('dbo.IdxIdem') AND i.name = 'UX_IdxIdem'");

        Deploy(Package("IdxIdem", ", \"IgnoreDuplicateKey\": true, \"PadIndex\": true, \"FillFactor\": 70"));

        Assert.Multiple(() =>
        {
            Assert.That(IndexFlag("IdxIdem", "ignore_dup_key"), Is.EqualTo(1), "still set after a re-deploy");
            Assert.That(IndexFlag("IdxIdem", "is_padded"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                               + "WHERE ObjectType = 'index' AND ObjectName LIKE '%UX_IdxIdem%' "
                               + "AND ActionType IN ('dropped', 'created')"),
                Is.LessThanOrEqualTo(1),
                "the second deploy must not drop and recreate the index -- more than the initial create "
                + "means a site was missed and the package churns on every run");
            Assert.That(Scalar("SELECT i.index_id FROM sys.indexes i "
                               + "WHERE i.[object_id] = OBJECT_ID('dbo.IdxIdem') AND i.name = 'UX_IdxIdem'"),
                Is.EqualTo(firstId),
                "and it is the same index object, not a recreated one wearing the same name");
        });
    }

    [Test]
    public void TheOptions_RoundTripThroughExtraction()
    {
        Deploy(Package("IdxRound", ", \"IgnoreDuplicateKey\": true, \"PadIndex\": true, \"FillFactor\": 70"));

        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.GenerateTableJson @p_Schema = 'dbo', @p_Table = 'IdxRound'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        var json = sb.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"IgnoreDuplicateKey\": true").IgnoreCase,
                "an extracted package that drops the option re-deploys an index that rejects duplicate "
                + "inserts the original accepted.\n" + json);
            Assert.That(json, Does.Contain("\"PadIndex\": true").IgnoreCase, json);
        });
    }

    [Test]
    public void ChangingAnOption_IsApplied()
    {
        // The other half of idempotence: unchanged must not churn, but CHANGED must actually take.
        // A comparison that ignored these entirely would pass the idempotence test perfectly.
        Deploy(Package("IdxChange", ", \"IgnoreDuplicateKey\": true"));
        Assert.That(IndexFlag("IdxChange", "ignore_dup_key"), Is.EqualTo(1), "precondition");

        Deploy(Package("IdxChange", ", \"IgnoreDuplicateKey\": false"));

        Assert.That(IndexFlag("IdxChange", "ignore_dup_key"), Is.Zero,
            "turning it off in the package has to turn it off on the server");
    }
}
