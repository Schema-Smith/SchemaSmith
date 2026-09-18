// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Bootstrap converges an index's SHAPE, not merely its name (SQL Server).
/// <para>Bootstrap used to ask only "does an index of this name exist?". An index created by an older
/// SchemaSmith, or by hand, therefore kept whatever shape it had while the kindling JSON's declaration quietly
/// did not hold. Shape here is uniqueness, clustering and the key column list, read from <c>sys.indexes</c> /
/// <c>sys.index_columns</c> rather than from text.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class BootstrapIndexShapeTests
{
    private const string TableName = "BootstrapShapeProbe";
    private const string IndexName = "IX_BootstrapShapeProbe";

    private IDbConnection _connection = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp() => Exec($"IF OBJECT_ID('dbo.{TableName}') IS NOT NULL DROP TABLE dbo.[{TableName}]");

    [TearDown]
    public void TearDown() => Exec($"IF OBJECT_ID('dbo.{TableName}') IS NOT NULL DROP TABLE dbo.[{TableName}]");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        cmd.ExecuteNonQuery();
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private static string Json(bool unique, string indexColumns, bool clustered = false) =>
        "{"
        + $"\"Schema\": \"dbo\", \"Name\": \"[{TableName}]\","
        + "\"Columns\": ["
        + "{\"Name\": \"[Alpha]\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false},"
        + "{\"Name\": \"[Beta]\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false}],"
        + "\"Indexes\": ["
        + $"{{\"Name\": \"[{IndexName}]\", \"Unique\": {(unique ? "true" : "false")}, \"Clustered\": {(clustered ? "true" : "false")}, \"IndexColumns\": \"{indexColumns}\"}}]"
        + "}";

    /// <summary>is_unique|CLUSTERED?|key columns in key order — everything bootstrap's JSON can declare.</summary>
    private string Shape() =>
        ScalarStr($@"SELECT CAST(si.is_unique AS VARCHAR(1)) + '|'
                          + CASE WHEN si.type_desc = 'CLUSTERED' THEN 'C' ELSE 'N' END + '|'
                          + ISNULL(STUFF((SELECT ',' + c.[name]
                                            FROM sys.index_columns ic
                                            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                           WHERE ic.object_id = si.object_id AND ic.index_id = si.index_id
                                             AND ic.is_included_column = 0
                                           ORDER BY ic.key_ordinal
                                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''), '')
                       FROM sys.indexes si
                      WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'");

    private void CallBootstrap(string json)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SchemaSmith.BootstrapTableQuench";
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = 120;
        var p = cmd.CreateParameter();
        p.ParameterName = "@TableDefinitions";
        p.Value = json;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    [TestCase("0|N|Alpha", true, "[Alpha],[Beta]", "1|N|Alpha,Beta", TestName = "NotUniqueAndMissingAKey")]
    [TestCase("1|N|Alpha,Beta", false, "[Alpha],[Beta]", "0|N|Alpha,Beta", TestName = "UniqueWhenTheDeclarationIsNot")]
    [TestCase("1|N|Beta,Alpha", true, "[Alpha],[Beta]", "1|N|Alpha,Beta", TestName = "KeysInTheWrongOrder")]
    public void AnIndexWhoseDeployedShapeDiffers_IsRebuiltToMatchTheDeclaration(
        string deployedShape, bool declaredUnique, string declaredColumns, string expectedShape)
    {
        var parts = deployedShape.Split('|');
        Exec($"CREATE TABLE dbo.[{TableName}] ([Alpha] VARCHAR(64) NOT NULL, [Beta] VARCHAR(64) NOT NULL)");
        Exec($"CREATE {(parts[0] == "1" ? "UNIQUE " : "")}NONCLUSTERED INDEX [{IndexName}] ON dbo.[{TableName}] "
             + "(" + string.Join(", ", Array.ConvertAll(parts[2].Split(','), c => $"[{c}]")) + ")");
        Assert.That(Shape(), Is.EqualTo(deployedShape), "setup: the wrong shape must actually be deployed first");

        CallBootstrap(Json(declaredUnique, declaredColumns));

        Assert.That(Shape(), Is.EqualTo(expectedShape),
            "bootstrap must rebuild an index whose deployed shape does not match its declaration");
    }

    [Test]
    public void AnIndexThatAlreadyMatches_IsNotRebuilt()
    {
        var json = Json(unique: true, indexColumns: "[Alpha],[Beta]");
        CallBootstrap(json);
        Assert.That(Shape(), Is.EqualTo("1|N|Alpha,Beta"), "setup: bootstrap must create the declared index");

        var before = ScalarStr($@"SELECT CAST(si.index_id AS VARCHAR(10)) FROM sys.indexes si
                                   WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'");
        CallBootstrap(json);
        CallBootstrap(json);

        Assert.Multiple(() =>
        {
            Assert.That(Shape(), Is.EqualTo("1|N|Alpha,Beta"), "the shape must survive repeat calls");
            Assert.That(ScalarStr($@"SELECT CAST(si.index_id AS VARCHAR(10)) FROM sys.indexes si
                                      WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'"),
                Is.EqualTo(before), "a matching index must not be dropped and recreated on every kindle");
        });
    }

    // A unique CONSTRAINT cannot be dropped with DROP INDEX; bootstrap has to drop it as the constraint it is.
    [Test]
    public void AKeyBackedByAUniqueConstraint_IsRebuiltRatherThanFailing()
    {
        Exec($@"CREATE TABLE dbo.[{TableName}] ([Alpha] VARCHAR(64) NOT NULL, [Beta] VARCHAR(64) NOT NULL);
                ALTER TABLE dbo.[{TableName}] ADD CONSTRAINT [{IndexName}] UNIQUE ([Alpha]);");
        Assert.That(Shape(), Is.EqualTo("1|N|Alpha"), "setup: a unique constraint backs the index");

        Assert.DoesNotThrow(() => CallBootstrap(Json(unique: true, indexColumns: "[Alpha],[Beta]")),
            "dropping a constraint-backed key with DROP INDEX fails; bootstrap must use ALTER TABLE DROP CONSTRAINT");

        Assert.That(Shape(), Is.EqualTo("1|N|Alpha,Beta"), "and the declared shape must end up deployed");
    }

    // The legacy (XML) kindle path installs its own twin of this procedure, and nothing else exercises it. It is
    // tested the way it is actually used: kindle a scratch database in XML mode, break a SchemaSmith-owned index's
    // shape, and re-kindle. IX_ChangeAudit_SessionId is declared non-unique on [SessionId].
    [Test]
    public void TheLegacyXmlBootstrap_ConvergesShapeToo()
    {
        var db = $"BootstrapShapeXml_{Guid.NewGuid():N}"[..28];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"CREATE DATABASE [{db}]";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(db);
            Schema.Utility.ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true,
                encoding: Schema.Utility.IngestEncoding.Xml);

            string Shape()
            {
                cmd.CommandText = @"SELECT CAST(si.is_unique AS VARCHAR(1)) + '|'
                                         + ISNULL(STUFF((SELECT ',' + c.[name]
                                                           FROM sys.index_columns ic
                                                           JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                                          WHERE ic.object_id = si.object_id AND ic.index_id = si.index_id
                                                            AND ic.is_included_column = 0
                                                          ORDER BY ic.key_ordinal
                                                          FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''), '')
                                      FROM sys.indexes si
                                     WHERE si.object_id = OBJECT_ID('SchemaSmith.ChangeAudit')
                                       AND si.[name] = 'IX_ChangeAudit_SessionId'";
                return cmd.ExecuteScalar() as string;
            }

            Assert.That(Shape(), Is.EqualTo("0|SessionId"), "setup: the XML kindle must create the declared index");

            // the wrong shape a hand edit or an older build could leave behind
            cmd.CommandText = @"DROP INDEX [IX_ChangeAudit_SessionId] ON SchemaSmith.ChangeAudit;
                                CREATE UNIQUE INDEX [IX_ChangeAudit_SessionId] ON SchemaSmith.ChangeAudit ([Id]);";
            cmd.ExecuteNonQuery();
            Assert.That(Shape(), Is.EqualTo("1|Id"), "setup: the shape must really be wrong before re-kindling");

            Schema.Utility.ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true,
                encoding: Schema.Utility.IngestEncoding.Xml);

            Assert.That(Shape(), Is.EqualTo("0|SessionId"),
                "the legacy XML bootstrap must converge shape exactly as the JSON one does");
        }
        finally
        {
            conn.ChangeDatabase("master");
            cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            cmd.ExecuteNonQuery();
        }
    }

    // Clustering is declarable and the comparison reads it, so it is tested both ways -- a declared CLUSTERED index
    // deployed as NONCLUSTERED, and the reverse. Nothing else covered it.
    [TestCase(false, true, "0|C|Alpha,Beta", TestName = "DeployedNonclusteredButDeclaredClustered")]
    [TestCase(true, false, "0|N|Alpha,Beta", TestName = "DeployedClusteredButDeclaredNonclustered")]
    public void AnIndexWhoseClusteringDiffers_IsRebuilt(bool deployClustered, bool declareClustered, string expectedShape)
    {
        Exec($"CREATE TABLE dbo.[{TableName}] ([Alpha] VARCHAR(64) NOT NULL, [Beta] VARCHAR(64) NOT NULL)");
        Exec($"CREATE {(deployClustered ? "CLUSTERED" : "NONCLUSTERED")} INDEX [{IndexName}] ON dbo.[{TableName}] ([Alpha], [Beta])");
        Assert.That(Shape(), Is.EqualTo(deployClustered ? "0|C|Alpha,Beta" : "0|N|Alpha,Beta"),
            "setup: the wrong clustering must actually be deployed first");

        CallBootstrap(Json(unique: false, indexColumns: "[Alpha],[Beta]", clustered: declareClustered));

        Assert.That(Shape(), Is.EqualTo(expectedShape), "a clustering difference must be rebuilt");
    }

    // Sort direction is part of the shape on both sides.
    [TestCase("[Alpha] DESC,[Beta]")]
    [TestCase("[Alpha] ASC,[Beta]")]
    public void AnIndexDeclaredWithASortDirection_IsNotRebuilt(string declaredColumns)
    {
        var json = Json(unique: false, indexColumns: declaredColumns);
        CallBootstrap(json);
        var before = ScalarStr($@"SELECT CAST(si.index_id AS VARCHAR(10)) FROM sys.indexes si
                                   WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'");
        Assert.That(before, Is.Not.Null, "setup: bootstrap must create the declared index");

        CallBootstrap(json);
        CallBootstrap(json);

        Assert.That(ScalarStr($@"SELECT CAST(si.index_id AS VARCHAR(10)) FROM sys.indexes si
                                  WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'"),
            Is.EqualTo(before),
            $"'{declaredColumns}' must compare equal to itself rather than being rebuilt on every kindle");
    }

    // A filtered index is a shape bootstrap cannot declare, so one deployed under a declared name is not what the
    // declaration asks for -- and a filtered UNIQUE index is exactly how a one-owner-style invariant gets quietly
    // weakened by hand.
    [Test]
    public void AFilteredIndexUnderADeclaredName_IsRebuilt()
    {
        Exec($"CREATE TABLE dbo.[{TableName}] ([Alpha] VARCHAR(64) NOT NULL, [Beta] VARCHAR(64) NOT NULL)");
        Exec($"CREATE UNIQUE NONCLUSTERED INDEX [{IndexName}] ON dbo.[{TableName}] ([Alpha], [Beta]) WHERE [Alpha] <> 'x'");
        Assert.That(ScalarStr($@"SELECT CAST(si.has_filter AS VARCHAR(1)) FROM sys.indexes si
                                  WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'"),
            Is.EqualTo("1"), "setup: the deployed index must really be filtered");

        CallBootstrap(Json(unique: true, indexColumns: "[Alpha],[Beta]"));

        Assert.That(ScalarStr($@"SELECT CAST(si.has_filter AS VARCHAR(1)) FROM sys.indexes si
                                  WHERE si.object_id = OBJECT_ID('dbo.{TableName}') AND si.[name] = '{IndexName}'"),
            Is.EqualTo("0"), "a filtered index must be rebuilt as the unfiltered one the declaration describes");
    }

    // A PRIMARY KEY cannot be dropped with DROP INDEX and is never rebuilt by the create pass, so it gets its
    // own swap -- DROP CONSTRAINT then ADD CONSTRAINT inside one transaction, leaving the rows alone.
    [Test]
    public void APrimaryKeyWhoseShapeDiffers_IsSwappedInPlace()
    {
        Exec($@"CREATE TABLE dbo.[{TableName}] ([Alpha] VARCHAR(64) NOT NULL, [Beta] VARCHAR(64) NOT NULL,
                        CONSTRAINT [PK_{TableName}] PRIMARY KEY NONCLUSTERED ([Alpha]));
                INSERT INTO dbo.[{TableName}] VALUES ('a', '1'), ('b', '2');");
        var json = PkJson("[Alpha],[Beta]");

        CallBootstrap(json);

        Assert.Multiple(() =>
        {
            Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha,Beta"), "the declared key must be deployed");
            Assert.That(ScalarStr($"SELECT COUNT(*) FROM dbo.[{TableName}]"), Is.EqualTo("2"),
                "and the rows must survive -- a PK change must not be a table rebuild");
        });

        CallBootstrap(json);
        Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha,Beta"), "and a second call must change nothing");
    }

    [Test]
    public void APrimaryKeySwapBlockedByData_RefusesWithTheOldKeyIntact()
    {
        Exec($@"CREATE TABLE dbo.[{TableName}] ([Alpha] VARCHAR(64) NOT NULL, [Beta] VARCHAR(64) NOT NULL,
                        CONSTRAINT [PK_{TableName}] PRIMARY KEY NONCLUSTERED ([Alpha]));
                INSERT INTO dbo.[{TableName}] VALUES ('a', '1'), ('b', '1');");

        var ex = Assert.Catch<Exception>(() => CallBootstrap(PkJson("[Beta]")));

        Assert.Multiple(() =>
        {
            // SchemaSmith's own message, not the engine's: the transaction means the key survives either way,
            // so what the pre-check buys is a refusal a DBA can act on without decoding a constraint error.
            Assert.That(ex!.Message, Does.Contain("SchemaSmith bootstrap: cannot rebuild PRIMARY KEY").And.Contain(TableName),
                "the refusal must be SchemaSmith's own, naming the table and the remedy: " + ex.Message);
            Assert.That(PrimaryKeyColumns(), Is.EqualTo("Alpha"), "and the existing key must be untouched");
        });
    }

    private string PrimaryKeyColumns() =>
        ScalarStr($@"SELECT STUFF((SELECT ',' + c.[name]
                                     FROM sys.index_columns ic
                                     JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                     JOIN sys.key_constraints kc ON kc.parent_object_id = ic.object_id
                                                                AND kc.unique_index_id = ic.index_id
                                    WHERE kc.parent_object_id = OBJECT_ID('dbo.{TableName}') AND kc.[type] = 'PK'
                                    ORDER BY ic.key_ordinal
                                    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '')");

    private static string PkJson(string keyColumns) =>
        "{"
        + $"\"Schema\": \"dbo\", \"Name\": \"[{TableName}]\","
        + "\"Columns\": ["
        + "{\"Name\": \"[Alpha]\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false},"
        + "{\"Name\": \"[Beta]\", \"DataType\": \"VARCHAR(64)\", \"Nullable\": false}],"
        + "\"Indexes\": ["
        + $"{{\"Name\": \"[PK_{TableName}]\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"{keyColumns}\"}}]"
        + "}";
}
