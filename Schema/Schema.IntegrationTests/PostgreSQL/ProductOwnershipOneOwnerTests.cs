// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// One owner per tracked object, ever — structural parity with SQL Server's single ProductName extended
/// property and MySQL's <c>uk_object</c>. A tracked object is a table (NULL <c>IndexName</c>) or an index, and
/// two owner rows for one of them is an invalid state that must be impossible, not merely unwritten.
/// <para><b>Kindling alone must enforce it.</b> The invariant is declared in
/// <c>Kindling_ProductOwnership.json</c> as <c>"NullsNotDistinct": true</c>, so <c>BootstrapTableQuench</c> has
/// to honour it — it was carried by a transitional migration script for two releases, which meant a database
/// that never ran that script (a fresh one included) got a plain unique index where NULL never equals NULL,
/// and a table could take two owners. These tests kindle and then try to violate the invariant.</para>
/// <para><b>Two forms, one meaning.</b> <c>NULLS NOT DISTINCT</c> is PostgreSQL 15+. At the supported floor the
/// same invariant is a functional unique index over <c>COALESCE(IndexName, '')</c> — a table folds to '' so it
/// collides with itself, and an index can never be named '', so the two key spaces never cross.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class ProductOwnershipOneOwnerTests
{
    private IDbConnection _connection = null!;
    private IDbCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _command = _connection.CreateCommand();

        // Kindle onto a clean slate: this asserts what kindling itself produces, so an index left behind by an
        // earlier fixture would make the whole thing vacuous.
        _command.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""ProductOwnership""";
        _command.ExecuteNonQuery();
        ForgeKindler.KindleTheForge(_command, Platform.PostgreSQL, forceReKindle: true);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            _command.CommandText = @"DELETE FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'OneOwnerProbe'";
            _command.ExecuteNonQuery();
        }
        finally
        {
            _command.Dispose();
            _connection.Close();
            _connection.Dispose();
        }
    }

    private int ServerMajor()
    {
        _command.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        return Convert.ToInt32(_command.ExecuteScalar());
    }

    private string IndexDef()
    {
        _command.CommandText = @"SELECT indexdef FROM pg_indexes
                                  WHERE schemaname = 'SchemaSmith' AND indexname = 'PK_ProductOwnership'";
        return _command.ExecuteScalar() as string;
    }

    private void InsertOwner(string product, string indexName = null)
    {
        _command.CommandText = $@"INSERT INTO ""SchemaSmith"".""ProductOwnership""
                                  (""Schema"", ""TableName"", ""IndexName"", ""ProductName"", ""template_name"")
                                  VALUES ('dbo', 'OneOwnerProbe', {(indexName == null ? "NULL" : $"'{indexName}'")}, '{product}', '')";
        _command.ExecuteNonQuery();
    }

    [Test]
    public void Kindling_CreatesTheOneOwnerIndex_InTheFormThisServerSupports()
    {
        var def = IndexDef();

        Assert.That(def, Is.Not.Null, "kindling must create PK_ProductOwnership");
        if (ServerMajor() >= 15)
            Assert.That(def, Does.Contain("NULLS NOT DISTINCT"),
                "PG15+ enforces the invariant with NULLS NOT DISTINCT: " + def);
        else
            Assert.That(def, Does.Contain("COALESCE"),
                "below PG15 the same invariant is a functional index over COALESCE(IndexName, ''): " + def);
    }

    [Test]
    public void ATable_CannotTakeASecondOwner()
    {
        InsertOwner("ProductA");

        var ex = Assert.Catch<Exception>(() => InsertOwner("ProductB"));

        Assert.That(ex!.Message, Does.Contain("PK_ProductOwnership"),
            "a second owner row for the same table (NULL IndexName) must violate the one-owner index: " + ex.Message);
    }

    [Test]
    public void AnIndex_CannotTakeASecondOwner()
    {
        InsertOwner("ProductA", indexName: "IX_OneOwnerProbe");

        var ex = Assert.Catch<Exception>(() => InsertOwner("ProductB", indexName: "IX_OneOwnerProbe"));

        Assert.That(ex!.Message, Does.Contain("PK_ProductOwnership"), ex.Message);
    }

    // The two key spaces must not cross: a table (NULL) and an index named on the same table are different
    // objects and each gets its own owner row.
    [Test]
    public void ATableAndOneOfItsIndexes_AreSeparateObjects()
    {
        InsertOwner("ProductA");
        Assert.DoesNotThrow(() => InsertOwner("ProductA", indexName: "IX_OneOwnerProbe"));

        _command.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'OneOwnerProbe'";
        Assert.That(Convert.ToInt32(_command.ExecuteScalar()), Is.EqualTo(2));
    }

    [Test]
    public void ReKindling_LeavesTheIndexInPlace()
    {
        var before = IndexDef();

        ForgeKindler.KindleTheForge(_command, Platform.PostgreSQL, forceReKindle: true);

        Assert.That(IndexDef(), Is.EqualTo(before), "a repeat kindle must not drop or reshape the index");
    }
}
