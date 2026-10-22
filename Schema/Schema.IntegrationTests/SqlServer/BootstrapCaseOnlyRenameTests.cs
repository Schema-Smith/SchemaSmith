// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// On a case-insensitive database an OldName that differs from Name only in case names the same object. Read as two
/// names, both "existed" and BootstrapTableQuench refused the rename as a clash. The suite's main database is
/// deliberately case-sensitive, where the same declaration is an ordinary rename, so this fixture builds a
/// case-insensitive one and installs only the bootstrap procedure, which has no other dependencies. It runs once per
/// ingest encoding: the XML twin is a separate copy of the same logic, under the same procedure name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture(IngestEncoding.Json)]
[TestFixture(IngestEncoding.Xml)]
public class BootstrapCaseOnlyRenameTests(IngestEncoding encoding)
{
    private IDbConnection _master = null!;
    private IDbConnection _connection = null!;
    private string _db = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _master = Open("master");
        _db = $"BootCi_{Guid.NewGuid():N}"[..30];
        Exec(_master, $"CREATE DATABASE [{_db}] COLLATE Latin1_General_CI_AS");

        _connection = Open(_db);
        Exec(_connection, "CREATE SCHEMA SchemaSmith");
        var script = encoding == IngestEncoding.Xml ? "SchemaSmith.BootstrapTableXmlQuench.sql" : "SchemaSmith.BootstrapTableQuench.sql";
        foreach (var batch in SqlScript.SplitBatches(ResourceLoader.Load(script, Platform.SqlServer), Platform.SqlServer))
            if (!string.IsNullOrWhiteSpace(batch)) Exec(_connection, batch);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Dispose();
        SqlConnection.ClearAllPools();
        if (_master == null) return;
        using (var cmd = _master.CreateCommand()) TestDatabaseDrop.Drop(cmd, _db);
        _master.Dispose();
    }

    [SetUp]
    public void SetUp() => Exec(_connection, "DROP TABLE IF EXISTS dbo.CaseOnly");

    private static IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
            new SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = database }.ConnectionString);
        conn.Open();
        return conn;
    }

    private static void Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void CallBootstrap(string tableOldName, string columnOldName)
    {
        var json = "{\"Schema\": \"[dbo]\", \"Name\": \"[CaseOnly]\""
            + (tableOldName == null ? "" : $", \"OldName\": \"[{tableOldName}]\"")
            + ", \"Columns\": [ {\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false},"
            + " {\"Name\": \"[Value]\", \"DataType\": \"VARCHAR(50)\", \"Nullable\": false"
            + (columnOldName == null ? "" : $", \"OldName\": \"[{columnOldName}]\"")
            + "} ], \"Indexes\": [ {\"Name\": \"[PK_CaseOnly]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"} ] }";
        var payload = encoding == IngestEncoding.Xml ? ModelXmlSerializer.ToIngestXmlObject(json, "Table") : json;
        Exec(_connection, $"EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = N'{payload.Replace("'", "''")}'");
    }

    [Test]
    public void ColumnRename_CaseOnlyOldName_RespellsTheColumnAndDataSurvives()
    {
        Exec(_connection, "CREATE TABLE dbo.CaseOnly ([Id] INT NOT NULL PRIMARY KEY, [value] VARCHAR(50) NOT NULL); INSERT dbo.CaseOnly VALUES (1, 'kept')");

        CallBootstrap(null, "value");
        Assert.DoesNotThrow(() => CallBootstrap(null, "value"), "the second run finds the column already spelled as declared");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CaseOnly') AND column_id = 2"),
                Is.EqualTo("Value"), "the column must carry the declared spelling");
            Assert.That(Scalar("SELECT [Value] FROM dbo.CaseOnly WHERE [Id] = 1"), Is.EqualTo("kept"));
        });
    }

    [Test]
    public void TableRename_CaseOnlyOldName_RespellsTheTableAndDataSurvives()
    {
        Exec(_connection, "CREATE TABLE dbo.caseonly ([Id] INT NOT NULL PRIMARY KEY, [Value] VARCHAR(50) NOT NULL); INSERT dbo.caseonly VALUES (1, 'kept')");

        CallBootstrap("caseonly", null);
        Assert.DoesNotThrow(() => CallBootstrap("caseonly", null), "the second run finds the table already spelled as declared");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT name FROM sys.tables WHERE object_id = OBJECT_ID('dbo.CaseOnly')"),
                Is.EqualTo("CaseOnly"), "the table must carry the declared spelling");
            Assert.That(Scalar("SELECT [Value] FROM dbo.CaseOnly WHERE [Id] = 1"), Is.EqualTo("kept"));
        });
    }
}
