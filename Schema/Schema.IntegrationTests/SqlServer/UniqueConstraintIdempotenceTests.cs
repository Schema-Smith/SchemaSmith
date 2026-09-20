// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// A UNIQUE constraint declared with <c>UniqueConstraint</c> must survive a re-deploy untouched.
/// <para>Written after a near-miss while building FILESTREAM support, and the near-miss is the point.
/// A second way of declaring ROWGUIDCOL was briefly added — a boolean column property — alongside the
/// one that already existed. The two disagreed: packages said <c>UNIQUEIDENTIFIER</c> while the deployed
/// column reconstructed as <c>UNIQUEIDENTIFIER ROWGUIDCOL</c>, so every re-deploy saw the column as
/// changed and dropped every index on it as "modified". On an ordinary table that is invisible — the
/// drop and the recreate both succeed, the run is green, and the only cost is churn nobody notices.
/// It became visible only because a FILESTREAM column depends on that constraint and SQL Server refuses
/// the drop outright (error 5505) on a package that had not changed at all.</para>
/// <para>So this guards a whole class of mistake rather than one bug: any column property that fails to
/// round-trip turns a no-op deploy into silent drop-and-recreate churn, and nothing in a green run says
/// so.</para>
/// <para>Asserted on <c>object_id</c> rather than on the constraint merely existing, because a
/// drop-and-recreate leaves a constraint of the same name behind — the exact reason this went unnoticed.
/// The identity is what proves it was left alone (CLAUDE.md Rule 32: assert the outcome, not the
/// mechanism).</para>
/// <para>The guid column is declared <c>UNIQUEIDENTIFIER ROWGUIDCOL</c> deliberately, and that is what
/// makes this test bite. ROWGUIDCOL rides the <c>DataType</c> string, the way IDENTITY does, and
/// <c>fn_ColumnTypeArguments</c> reconstructs it from <c>sys.columns.is_rowguidcol</c> when comparing
/// declared against deployed. Express it any other way and the two sides disagree on every run: the
/// column reads as changed, every index on it is dropped as "modified", and the package never converges.
/// A plain <c>UNIQUEIDENTIFIER</c> here would pass whether or not that round trip works.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class UniqueConstraintIdempotenceTests
{
    private IDbConnection _connection;
    // (no private database -- see OneTimeSetUp)

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[UqIdem]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[G]", "DataType": "UNIQUEIDENTIFIER ROWGUIDCOL", "Nullable": false, "Default": "NEWID()" }
            ],
            "Indexes": [
                { "Name": "[PK_UqIdem]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true },
                { "Name": "[UQ_UqIdem_G]", "IndexColumns": "[G]", "UniqueConstraint": true }
            ]
        }]
        """;

    // Uses the suite's already-kindled database rather than creating and kindling its own.
    //
    // A private database is not free: CREATE DATABASE, then kindling ~40 helper scripts, then SQL Server
    // compiling ModifiedTableQuench's plan from scratch -- plans are cached per object PER DATABASE, so a
    // fresh database never reuses one, and that compile alone measured ~2.6s for ~117ms of real work. This
    // fixture ran ONE test and paid all of it: 9 seconds, almost entirely setup.
    //
    // Nothing here needs a private database -- no compatibility level, filegroup, CDC or change-tracking
    // configuration, which is what genuinely forces a fixture to own one. Isolation comes from the UqIdem_
    // name prefix instead, and the objects are dropped in teardown so a shared database does not
    // accumulate them.
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

    // Everything this fixture creates, by its own name prefix -- so sharing the suite database cannot
    // leak state into a sibling fixture or inherit it from one.
    private void DropOwnObjects()
    {
        Exec(@"IF OBJECT_ID('dbo.UqIdem', 'U') IS NOT NULL DROP TABLE dbo.UqIdem;
               DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'UqIdemTest';");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? 0 : Convert.ToInt64(r);
    }

    private void Deploy()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'UqIdemTest', "
                          + $"@TableDefinitions = N'{TableJson.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void AUniqueConstraint_IsNotDroppedAndRecreated_OnAnUnchangedRedeploy()
    {
        Deploy();
        var first = Scalar("SELECT CONVERT(BIGINT, object_id) FROM sys.objects "
                           + "WHERE name = 'UQ_UqIdem_G' AND type = 'UQ'");
        Assert.That(first, Is.Not.Zero, "precondition: the unique constraint deployed as a constraint");

        Deploy();
        var second = Scalar("SELECT CONVERT(BIGINT, object_id) FROM sys.objects "
                            + "WHERE name = 'UQ_UqIdem_G' AND type = 'UQ'");

        Assert.That(second, Is.EqualTo(first),
            "an unchanged package must leave the constraint alone. A different object_id means it was "
            + "dropped and recreated, which is invisible on an ordinary table but fails outright once "
            + "anything depends on the constraint -- a FILESTREAM column makes the drop illegal (5505).");
    }
}
