// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// Bootstrap converges an index's SHAPE, not merely its name (PostgreSQL).
/// <para>This is the engine where it mattered most. <c>ProductOwnership</c>'s one-owner invariant is declared as
/// <c>"NullsNotDistinct": true</c>, and for two releases it was delivered by a transitional migration script
/// rather than by bootstrap — so the guarantee was only ever as good as the upgrade path taken, and a database
/// that never ran that script kept a plain unique index where NULL never equals NULL. Bootstrap now compares
/// uniqueness, the NULLS NOT DISTINCT form and the key signature against the catalog, and rebuilds what does not
/// match.</para>
/// <para><b>Two forms, one meaning.</b> <c>NULLS NOT DISTINCT</c> is PG15+; at the floor the same invariant is a
/// functional unique index over <c>COALESCE(col, '')</c>. The shape check has to accept whichever form this
/// server would itself create, which is why the expectation below is version-dependent.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class BootstrapIndexShapeTests
{
    private const string TableName = "BootstrapShapeProbe";
    private const string IndexName = "IX_BootstrapShapeProbe";

    private IDbConnection _connection = null!;
    private IDbCommand _command = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _command = _connection.CreateCommand();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _command?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp() => Exec($@"DROP TABLE IF EXISTS ""SchemaSmith"".""{TableName}"" CASCADE");

    [TearDown]
    public void TearDown() => Exec($@"DROP TABLE IF EXISTS ""SchemaSmith"".""{TableName}"" CASCADE");

    private void Exec(string sql)
    {
        _command.CommandText = sql;
        _command.ExecuteNonQuery();
    }

    private string ScalarStr(string sql)
    {
        _command.CommandText = sql;
        var result = _command.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private int ServerMajor() => Convert.ToInt32(ScalarStr("SELECT current_setting('server_version_num')::int / 10000"));

    private string IndexDef() =>
        ScalarStr($@"SELECT COALESCE((SELECT indexdef FROM pg_indexes
                                       WHERE schemaname = 'SchemaSmith' AND indexname = '{IndexName}'), '(none)')");

    private long IndexOid() =>
        Convert.ToInt64(ScalarStr($@"SELECT COALESCE((SELECT c.oid::bigint FROM pg_class c
                                                       JOIN pg_namespace n ON n.oid = c.relnamespace
                                                      WHERE n.nspname = 'SchemaSmith' AND c.relname = '{IndexName}'), 0)"));

    private static string Json(bool nullsNotDistinct) =>
        "{"
        + $"\"Schema\": \"SchemaSmith\", \"Name\": \"{TableName}\","
        + "\"Columns\": ["
        + "{\"Name\": \"Alpha\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false},"
        + "{\"Name\": \"Beta\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": true}],"
        + "\"Indexes\": ["
        + $"{{\"Name\": \"{IndexName}\", \"Unique\": true, \"NullsNotDistinct\": {(nullsNotDistinct ? "true" : "false")},"
        + " \"IndexColumns\": \"\\\"Alpha\\\",\\\"Beta\\\"\"}]"
        + "}";

    private void CallBootstrap(string json) =>
        Exec($@"CALL ""SchemaSmith"".""BootstrapTableQuench""('{json.Replace("'", "''")}')");

    private void CreateTable() =>
        Exec($@"CREATE TABLE ""SchemaSmith"".""{TableName}"" (""Alpha"" VARCHAR(64) NOT NULL, ""Beta"" VARCHAR(64) NULL)");

    // The exact shape a pre-v2.4.0 database carries: a PLAIN unique index, where NULL never equals NULL, so the
    // same logical object can take two rows.
    [Test]
    public void APlainUniqueIndexWhereTheDeclarationAsksForNullsNotDistinct_IsRebuilt()
    {
        CreateTable();
        Exec($@"CREATE UNIQUE INDEX ""{IndexName}"" ON ""SchemaSmith"".""{TableName}"" (""Alpha"", ""Beta"")");
        Exec($@"INSERT INTO ""SchemaSmith"".""{TableName}"" VALUES ('a', NULL), ('a', NULL)");
        Assert.That(ScalarStr($@"SELECT COUNT(*)::text FROM ""SchemaSmith"".""{TableName}"""), Is.EqualTo("2"),
            "setup: the old shape must really accept two rows that differ only by NULL");
        Exec($@"DELETE FROM ""SchemaSmith"".""{TableName}"" WHERE ctid NOT IN (SELECT MIN(ctid) FROM ""SchemaSmith"".""{TableName}"")");

        CallBootstrap(Json(nullsNotDistinct: true));

        var def = IndexDef();
        if (ServerMajor() >= 15)
            Assert.That(def, Does.Contain("NULLS NOT DISTINCT"), "PG15+ must end up with the engine's own clause: " + def);
        else
            Assert.That(def, Does.Contain("COALESCE"), "below PG15 the same invariant is a functional index: " + def);

        var ex = Assert.Catch<Exception>(() => Exec($@"INSERT INTO ""SchemaSmith"".""{TableName}"" VALUES ('a', NULL)"));
        Assert.That(ex!.Message, Does.Contain(IndexName),
            "after the rebuild the duplicate must be refused -- that is the whole point of the declared shape");
    }

    [Test]
    public void AnIndexWithTheWrongKeyColumns_IsRebuilt()
    {
        CreateTable();
        Exec($@"CREATE UNIQUE INDEX ""{IndexName}"" ON ""SchemaSmith"".""{TableName}"" (""Alpha"")");

        CallBootstrap(Json(nullsNotDistinct: false));

        Assert.That(IndexDef(), Does.Contain("Alpha").And.Contain("Beta"),
            "a key list that does not match the declaration must be rebuilt: " + IndexDef());
    }

    [Test]
    public void AnIndexThatAlreadyMatches_IsNotRebuilt()
    {
        var json = Json(nullsNotDistinct: true);
        CreateTable();
        CallBootstrap(json);
        var first = IndexOid();
        Assert.That(first, Is.Not.Zero, "setup: bootstrap must create the declared index");

        CallBootstrap(json);
        CallBootstrap(json);

        Assert.That(IndexOid(), Is.EqualTo(first),
            "a matching index must not be dropped and recreated on every kindle");
    }

    // A hand-added UNIQUE CONSTRAINT is backed by an index, and PostgreSQL refuses to DROP INDEX it ("cannot drop
    // index ... because constraint ... requires it"). "Created by hand" is precisely the case this step exists for,
    // so it must be dropped as the constraint it is rather than aborting the kindle.
    [Test]
    public void AnIndexBackingAUniqueConstraint_IsRebuiltRatherThanFailing()
    {
        CreateTable();
        Exec($@"ALTER TABLE ""SchemaSmith"".""{TableName}"" ADD CONSTRAINT ""{IndexName}"" UNIQUE (""Alpha"")");

        Assert.DoesNotThrow(() => CallBootstrap(Json(nullsNotDistinct: false)),
            "a constraint-backed index must not abort the kindle");

        Assert.Multiple(() =>
        {
            Assert.That(IndexDef(), Does.Contain("Alpha").And.Contain("Beta"),
                "the declared shape must end up deployed: " + IndexDef());
            Assert.That(ScalarStr($@"SELECT COUNT(*)::text FROM pg_constraint con
                                      JOIN pg_class rel ON rel.oid = con.conrelid
                                     WHERE rel.relname = '{TableName}' AND con.conname = '{IndexName}'"),
                Is.EqualTo("0"), "and the constraint that blocked the drop must be gone");
        });
    }

    // A partial index is a shape no bootstrap declaration can express -- and a partial UNIQUE index is precisely
    // how the one-owner invariant gets quietly weakened by hand, since its keys and uniqueness look right.
    [Test]
    public void APartialIndexUnderADeclaredName_IsRebuilt()
    {
        CreateTable();
        Exec($@"CREATE UNIQUE INDEX ""{IndexName}"" ON ""SchemaSmith"".""{TableName}"" (""Alpha"", ""Beta"")
                 WHERE ""Beta"" IS NOT NULL");
        Assert.That(IndexDef(), Does.Contain("WHERE"), "setup: the deployed index must really be partial");

        CallBootstrap(Json(nullsNotDistinct: false));

        Assert.That(IndexDef(), Does.Not.Contain("WHERE"),
            "a partial index must be rebuilt as the full one the declaration describes: " + IndexDef());
    }

    // An index of the same name on ANOTHER table in the same schema must not be touched: index names are unique
    // per schema, not per table, so a name-only lookup would compare -- and drop -- the wrong table's index.
    [Test]
    public void AnIndexOfTheSameNameOnAnotherTable_IsLeftAlone()
    {
        CreateTable();
        Exec($@"CREATE TABLE ""SchemaSmith"".""{TableName}_Other"" (""Alpha"" VARCHAR(64) NOT NULL)");
        try
        {
            Exec($@"CREATE UNIQUE INDEX ""{IndexName}_Other"" ON ""SchemaSmith"".""{TableName}_Other"" (""Alpha"")");
            var otherOid = ScalarStr($@"SELECT c.oid::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                                         WHERE n.nspname = 'SchemaSmith' AND c.relname = '{IndexName}_Other'");

            CallBootstrap(Json(nullsNotDistinct: true));

            Assert.That(ScalarStr($@"SELECT c.oid::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                                      WHERE n.nspname = 'SchemaSmith' AND c.relname = '{IndexName}_Other'"),
                Is.EqualTo(otherOid), "another table's index must be untouched");
        }
        finally
        {
            Exec($@"DROP TABLE IF EXISTS ""SchemaSmith"".""{TableName}_Other"" CASCADE");
        }
    }

    // A PRIMARY KEY is a constraint, so it is swapped with one ALTER TABLE carrying both the drop and the add --
    // atomic on this engine, and never a table rebuild.
    [Test]
    public void APrimaryKeyWhoseShapeDiffers_IsSwappedInPlace()
    {
        Exec($@"CREATE TABLE ""SchemaSmith"".""{TableName}"" (""Alpha"" VARCHAR(64) NOT NULL, ""Beta"" VARCHAR(64) NOT NULL,
                        CONSTRAINT ""PK_{TableName}"" PRIMARY KEY (""Alpha""));
                INSERT INTO ""SchemaSmith"".""{TableName}"" VALUES ('a', '1'), ('b', '2');");
        var json = PkJson(@"\""Alpha\"",\""Beta\""");

        CallBootstrap(json);

        Assert.Multiple(() =>
        {
            Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha,Beta"), "the declared key must be deployed");
            Assert.That(ScalarStr($@"SELECT COUNT(*)::text FROM ""SchemaSmith"".""{TableName}"""), Is.EqualTo("2"),
                "and the rows must survive -- a PK change must not be a table rebuild");
        });

        CallBootstrap(json);
        Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha,Beta"), "and a second call must change nothing");
    }

    [Test]
    public void APrimaryKeySwapBlockedByData_RefusesWithTheOldKeyIntact()
    {
        Exec($@"CREATE TABLE ""SchemaSmith"".""{TableName}"" (""Alpha"" VARCHAR(64) NOT NULL, ""Beta"" VARCHAR(64) NOT NULL,
                        CONSTRAINT ""PK_{TableName}"" PRIMARY KEY (""Alpha""));
                INSERT INTO ""SchemaSmith"".""{TableName}"" VALUES ('a', '1'), ('b', '1');");

        var ex = Assert.Catch<Exception>(() => CallBootstrap(PkJson(@"\""Beta\""")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("SchemaSmith bootstrap: cannot rebuild PRIMARY KEY").And.Contain(TableName),
                "the refusal must be SchemaSmith's own, naming the table and the remedy: " + ex.Message);
            Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha"), "and the existing key must be untouched");
        });
    }

    private string PrimaryKeyColumns() =>
        ScalarStr($@"SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                       FROM pg_constraint con
                       CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                       JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
                      WHERE con.conrelid = to_regclass('""SchemaSmith"".""{TableName}""') AND con.contype = 'p'");

    private static string PkJson(string keyColumns) =>
        "{"
        + $"\"Schema\": \"SchemaSmith\", \"Name\": \"{TableName}\","
        + "\"Columns\": ["
        + "{\"Name\": \"Alpha\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false},"
        + "{\"Name\": \"Beta\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false}],"
        + "\"Indexes\": ["
        + $"{{\"Name\": \"PK_{TableName}\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"{keyColumns}\"}}]"
        + "}";
}
