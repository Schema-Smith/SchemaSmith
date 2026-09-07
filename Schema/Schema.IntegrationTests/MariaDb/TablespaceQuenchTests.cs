// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// Tablespace (F2b) is MySQL-only -- MariaDB has no general tablespaces at any version (<c>CREATE
/// TABLESPACE ... ADD DATAFILE</c> is a syntax error there). This confirms the MySQL-only scoping holds on
/// the MariaDB side of every touchpoint, not just the domain's <c>Platforms</c> attribute (which governs
/// the generated <c>.schema</c> doc, not runtime behaviour):
/// <list type="bullet">
/// <item>Extraction never emits the key -- <c>SchemaSmith_TableTablespace</c>'s MariaDb override always
/// returns NULL.</item>
/// <item>A package that carries the key anyway (e.g. shared verbatim with a MySQL package, or
/// hand-authored) neither applies it nor false-refuses on redeploy. Both the CREATE-time emit
/// (<c>SchemaSmith_MissingTableAndColumnQuench</c>) and the tablespace-move refuse
/// (<c>SchemaSmith_ModifiedTableQuench</c> STEP -0.4) are gated on <c>VERSION() NOT LIKE '%MariaDB%'</c>.
/// Without that gate on the REFUSE side specifically, the always-NULL deployed read would make a declared
/// Tablespace look permanently "different from deployed" on MariaDB and refuse every redeploy forever --
/// the regression <see cref="RedeployingWithTheSameDeclaredTablespace_DoesNotFalseRefuse"/> pins.</item>
/// </list>
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class TablespaceQuenchTests
{
    private const string TableName = "mdb_tablespace_test";

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void DropTestTable() => Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{TableName}`");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void Deploy(string extraProps)
    {
        var json = "[{ \"Name\": \"`" + TableName + "`\", \"Engine\": \"InnoDB\"" + extraProps
                   + ", \"Columns\": [ { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false } ],"
                   + " \"Indexes\": [ { \"Name\": \"`pk_" + TableName + "`\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"`id`\" } ] }]";
        Exec($"CALL SchemaSmith_TableQuench('MdbTablespaceProduct', '{_testDb}', '{json.Replace("'", "''")}', 0, 0, 0)");
    }

    private string ExtractedJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{TableName}')";
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? "" : r.ToString();
    }

    [Test]
    public void ATableDeclaringTablespace_NeverAppliesItAndExtractsWithoutTheKey()
    {
        // MariaDB has no catalog concept of a general tablespace to place this table in, so the property
        // is quietly inert here -- same posture as any other MySQL-only property hand-authored (or
        // carried over from a shared package) into a MariaDB deploy, e.g. Compression/Encryption.
        Assert.DoesNotThrow(() => Deploy(", \"Tablespace\": \"some_mysql_tablespace\""));

        Assert.That(ExtractedJson(), Does.Not.Contain("Tablespace"), ExtractedJson());
    }

    [Test]
    public void RedeployingWithTheSameDeclaredTablespace_DoesNotFalseRefuse()
    {
        // SchemaSmith_TableTablespace's MariaDb override always returns NULL (no general tablespaces to
        // report), so without STEP -0.4's VERSION() NOT LIKE '%MariaDB%' guard, a declared Tablespace
        // would compare as "different from deployed" on EVERY redeploy and refuse forever -- for a
        // property MariaDB can never satisfy in the first place.
        Deploy(", \"Tablespace\": \"some_mysql_tablespace\"");

        Assert.DoesNotThrow(() => Deploy(", \"Tablespace\": \"some_mysql_tablespace\""));
    }
}
