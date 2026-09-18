// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Bootstrap converges an index's SHAPE, not merely its name (MySQL / MariaDB binding).
/// <para><b>Why this is bootstrap's job and not a migration script's.</b> Bootstrap used to ask only "does an
/// index of this name exist?" and create it when it did not. An index created by an older SchemaSmith, or by
/// hand, therefore kept whatever shape it had forever, while the declaration in the kindling JSON quietly did not
/// hold. That is how PostgreSQL's <c>ProductOwnership</c> one-owner invariant could be absent on a database that
/// had simply never run a since-deleted migration script -- the guarantee lived in a transitional script instead
/// of in bootstrap, so it was only ever as good as the upgrade path taken.</para>
/// <para>Shape is read from the catalog and compared only against what the JSON declares: uniqueness and the key
/// column list here; the other engines add their own (clustering on SQL Server, <c>NULLS NOT DISTINCT</c> on
/// PostgreSQL). Bootstrap keeps its no-dependency rule -- it runs before any other SchemaSmith object exists, so
/// the comparison is inline, not a helper function.</para>
/// </summary>
public abstract class BootstrapIndexShapeSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private const string TableName = "bootstrap_shape_test";
    private const string IndexName = "IX_bootstrap_shape_test";

    private IDbConnection _connection = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp() => Exec($"DROP TABLE IF EXISTS `{TableName}`");

    [TearDown]
    public void TearDown() => Exec($"DROP TABLE IF EXISTS `{TableName}`");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private void CallBootstrap(string json) =>
        Exec($"CALL SchemaSmith_BootstrapTableQuench('{json.Replace("'", "''")}')");

    // Bare names, as bootstrap's MySQL/MariaDB JSON contract uses them.
    private static string Json(bool unique, string indexColumns) =>
        "{"
        + $"\"Name\": \"{TableName}\","
        + "\"Columns\": ["
        + "{\"Name\": \"Id\", \"DataType\": \"INT\", \"Nullable\": false, \"AutoIncrement\": true, \"PrimaryKey\": true},"
        + "{\"Name\": \"Alpha\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false},"
        + "{\"Name\": \"Beta\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false}],"
        + "\"Indexes\": ["
        + "{\"Name\": \"PK_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"Id\"},"
        + $"{{\"Name\": \"{IndexName}\", \"Unique\": {(unique ? "true" : "false")}, \"IndexColumns\": \"{indexColumns}\"}}]"
        + "}";

    /// <summary>
    /// InnoDB's own index id, which is NOT reused when an index is dropped and recreated -- so this detects a
    /// same-shape rebuild, which neither SHOW CREATE TABLE nor information_schema.statistics can (both render the
    /// current logical definition, which a rebuild leaves identical). MySQL 8 exposes INNODB_INDEXES; MySQL 5.7
    /// and MariaDB spell it INNODB_SYS_INDEXES.
    /// </summary>
    private string IndexIdentity()
    {
        var modern = ScalarStr(@"SELECT COUNT(*) FROM information_schema.tables
                                  WHERE table_schema = 'information_schema' AND table_name = 'INNODB_INDEXES'") == "1";
        var idxView = modern ? "INNODB_INDEXES" : "INNODB_SYS_INDEXES";
        var tblView = modern ? "INNODB_TABLES" : "INNODB_SYS_TABLES";
        return ScalarStr($@"SELECT i.INDEX_ID FROM information_schema.{idxView} i
                              JOIN information_schema.{tblView} t ON t.TABLE_ID = i.TABLE_ID
                             WHERE t.NAME = '{MainDb}/{TableName}' AND i.NAME = '{IndexName}'");
    }

    private string Shape() =>
        ScalarStr($@"SELECT CONCAT(MIN(non_unique), '|', GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ','))
                       FROM information_schema.statistics
                      WHERE table_schema = '{MainDb}' AND table_name = '{TableName}' AND index_name = '{IndexName}'");

    // 0 = unique, so "0|Alpha,Beta" is a unique index over (Alpha, Beta).
    [TestCase("1|Alpha", true, "Alpha,Beta", "0|Alpha,Beta", TestName = "NotUniqueAndMissingAKey")]
    [TestCase("0|Alpha,Beta", false, "Alpha,Beta", "1|Alpha,Beta", TestName = "UniqueWhenTheDeclarationIsNot")]
    [TestCase("0|Beta,Alpha", true, "Alpha,Beta", "0|Alpha,Beta", TestName = "KeysInTheWrongOrder")]
    public void AnIndexWhoseDeployedShapeDiffers_IsRebuiltToMatchTheDeclaration(
        string deployedShape, bool declaredUnique, string declaredColumns, string expectedShape)
    {
        var parts = deployedShape.Split('|');
        var deployedUnique = parts[0] == "0";
        Exec($@"CREATE TABLE `{TableName}` (`Id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                                            `Alpha` VARCHAR(64) NOT NULL, `Beta` VARCHAR(64) NOT NULL)");
        Exec($"CREATE {(deployedUnique ? "UNIQUE " : "")}INDEX `{IndexName}` ON `{TableName}` ({string.Join(", ", Array.ConvertAll(parts[1].Split(','), c => $"`{c}`"))})");
        Assert.That(Shape(), Is.EqualTo(deployedShape), "setup: the wrong shape must actually be deployed first");

        CallBootstrap(Json(declaredUnique, declaredColumns));

        Assert.That(Shape(), Is.EqualTo(expectedShape),
            "bootstrap must rebuild an index whose deployed shape does not match its declaration");
    }

    // The other half: a matching index must be left alone, or every kindle rebuilds every index.
    [Test]
    public void AnIndexThatAlreadyMatches_IsNotRebuilt()
    {
        var json = Json(unique: true, indexColumns: "Alpha,Beta");
        CallBootstrap(json);
        Assert.That(Shape(), Is.EqualTo("0|Alpha,Beta"), "setup: bootstrap must create the declared index");

        var before = IndexIdentity();
        Assert.That(before, Is.Not.Null.And.Not.Empty, "setup: InnoDB must report an index id to compare");
        CallBootstrap(json);
        CallBootstrap(json);

        Assert.Multiple(() =>
        {
            Assert.That(Shape(), Is.EqualTo("0|Alpha,Beta"), "the shape must survive repeat calls");
            Assert.That(IndexIdentity(), Is.EqualTo(before),
                "a matching index must not be dropped and recreated on every kindle -- InnoDB does not reuse "
                + "an index id, so a changed id means it was rebuilt");
        });
    }

    // The shape the SHIPPED kindling actually declares. Kindling_CompletedMigrationScripts.json uses prefix
    // lengths -- ScriptPath(200), template_name(50), schema_name(50) -- and information_schema keeps the prefix
    // in sub_part, not in column_name. Comparing without it made a correct index unequal to itself, so two
    // indexes on the migration-history table were dropped and rebuilt on EVERY kindle, with the uniqueness that
    // guards against re-running a migration script unenforced in between.
    [Test]
    public void AnIndexDeclaredWithPrefixLengths_IsNotRebuilt()
    {
        var json = "{"
            + $"\"Name\": \"{TableName}\","
            + "\"Columns\": ["
            + "{\"Name\": \"Id\", \"DataType\": \"INT\", \"Nullable\": false, \"AutoIncrement\": true, \"PrimaryKey\": true},"
            + "{\"Name\": \"Alpha\", \"DataType\": \"VARCHAR(200)\", \"Nullable\": false},"
            + "{\"Name\": \"Beta\", \"DataType\": \"VARCHAR(200)\", \"Nullable\": false}],"
            + "\"Indexes\": ["
            + "{\"Name\": \"PK_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"Id\"},"
            + $"{{\"Name\": \"{IndexName}\", \"Unique\": true, \"IndexColumns\": \"Alpha(100),Beta(50)\"}}]"
            + "}";

        CallBootstrap(json);
        var before = IndexIdentity();
        Assert.That(before, Is.Not.Null.And.Not.Empty, "setup: bootstrap must create the declared index");

        CallBootstrap(json);
        CallBootstrap(json);

        Assert.That(IndexIdentity(), Is.EqualTo(before),
            "a prefix-length index must compare equal to itself -- otherwise every kindle rebuilds it");
    }

    // Sort direction is part of the shape on both sides: a declared DESC must not churn, and an ASC spelled out
    // in the declaration means what the catalog shows for it (nothing).
    [TestCase("Alpha DESC,Beta")]
    [TestCase("Alpha ASC,Beta")]
    public void AnIndexDeclaredWithASortDirection_IsNotRebuilt(string declaredColumns)
    {
        var json = Json(unique: false, indexColumns: declaredColumns);
        CallBootstrap(json);
        var before = IndexIdentity();
        Assert.That(before, Is.Not.Null.And.Not.Empty, "setup: bootstrap must create the declared index");

        CallBootstrap(json);
        CallBootstrap(json);

        Assert.That(IndexIdentity(), Is.EqualTo(before),
            $"'{declaredColumns}' must compare equal to itself rather than being rebuilt on every kindle");
    }

    // MySQL column names are case-insensitive, so the comparison must be too, or a declaration that differs only
    // in casing from the catalog's reporting rebuilds forever.
    [Test]
    public void AnIndexDeclaredInDifferentCase_IsNotRebuilt()
    {
        CallBootstrap(Json(unique: true, indexColumns: "Alpha,Beta"));
        var before = IndexIdentity();

        CallBootstrap(Json(unique: true, indexColumns: "alpha,beta"));

        Assert.That(IndexIdentity(), Is.EqualTo(before),
            "column names are case-insensitive on this engine; the comparison must not treat casing as a difference");
    }

    // Upgrading to UNIQUE over data that is not unique cannot succeed. DDL commits on this engine, so dropping
    // first would leave the table with NO index and a kindle that fails the same way forever.
    [Test]
    public void AUniqueUpgradeBlockedByData_RefusesWithTheOldIndexStillInPlace()
    {
        Exec($@"CREATE TABLE `{TableName}` (`Id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                                            `Alpha` VARCHAR(64) NOT NULL, `Beta` VARCHAR(64) NOT NULL)");
        Exec($"CREATE INDEX `{IndexName}` ON `{TableName}` (`Alpha`, `Beta`)");
        Exec($"INSERT INTO `{TableName}` (`Alpha`, `Beta`) VALUES ('a', 'b'), ('a', 'b')");

        var ex = Assert.Catch<Exception>(() => CallBootstrap(Json(unique: true, indexColumns: "Alpha,Beta")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("duplicate"),
                "the refusal must say why: " + ex.Message);
            Assert.That(Shape(), Is.EqualTo("1|Alpha,Beta"),
                "and the existing index must still be there -- dropping it would leave the table with neither");
        });
    }

    // A PRIMARY KEY is the one key whose drift matters most, and it cannot be handled by the drop-then-create
    // path: MySQL will not let an AUTO_INCREMENT column sit without a key even momentarily, so the swap is one
    // statement. The table's rows must survive it.
    [Test]
    public void APrimaryKeyWhoseShapeDiffers_IsSwappedInPlace()
    {
        Exec($@"CREATE TABLE `{TableName}` (`Alpha` VARCHAR(64) NOT NULL, `Beta` VARCHAR(64) NOT NULL,
                                            PRIMARY KEY (`Alpha`))");
        Exec($"INSERT INTO `{TableName}` (`Alpha`, `Beta`) VALUES ('a', '1'), ('b', '2')");
        var json = PkJson("`Alpha`,`Beta`");

        CallBootstrap(json);

        Assert.Multiple(() =>
        {
            Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha,Beta"), "the declared key must be deployed");
            Assert.That(ScalarStr($"SELECT COUNT(*) FROM `{TableName}`"), Is.EqualTo("2"),
                "and the rows must survive -- a PK change must not be a table rebuild");
        });

        CallBootstrap(json);
        Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha,Beta"), "and a second call must change nothing");
    }

    [Test]
    public void APrimaryKeySwapBlockedByData_RefusesWithTheOldKeyIntact()
    {
        Exec($@"CREATE TABLE `{TableName}` (`Alpha` VARCHAR(64) NOT NULL, `Beta` VARCHAR(64) NOT NULL,
                                            PRIMARY KEY (`Alpha`))");
        Exec($"INSERT INTO `{TableName}` (`Alpha`, `Beta`) VALUES ('a', '1'), ('b', '1')");

        var ex = Assert.Catch<Exception>(() => CallBootstrap(PkJson("`Beta`")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("SchemaSmith bootstrap: duplicate rows block").And.Contain(TableName),
                "the refusal must be SchemaSmith's own, naming the table and the remedy: " + ex.Message);
            Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha"), "and the existing key must be untouched");
        });
    }

    private string PrimaryKeyColumns() =>
        ScalarStr($@"SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',')
                       FROM information_schema.statistics
                      WHERE table_schema = '{MainDb}' AND table_name = '{TableName}' AND index_name = 'PRIMARY'");

    private static string PkJson(string keyColumns) =>
        "{"
        + $"\"Name\": \"{TableName}\","
        + "\"Columns\": ["
        + "{\"Name\": \"Alpha\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false},"
        + "{\"Name\": \"Beta\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false}],"
        + "\"Indexes\": ["
        + $"{{\"Name\": \"PRIMARY\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"{keyColumns}\"}}]"
        + "}";
}
