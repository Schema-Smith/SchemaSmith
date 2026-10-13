// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// FILESTREAM's degrade path, which — unlike the feature itself — CI can certify.
/// <para>SQL Server on Linux does not support FILESTREAM at all, and every CI engine job is a Linux
/// container. That makes the container the perfect place to test the *absence* case: the prerequisite
/// genuinely cannot be satisfied here, so this fixture exercises the real condition rather than a
/// simulated one. The deploy half lives in <c>FileStreamDeployTests</c>, which is
/// <c>[Explicit]</c> and needs a Windows instance.</para>
/// <para>FILESTREAM is the only one of the three database-scoped prerequisites that degrades to a
/// still-usable column: dropping the FILESTREAM clause leaves a plain <c>VARBINARY(MAX)</c>, so the data
/// still stores and reads and only its storage location changes. CDC and Change Tracking degrade to a
/// missing capability; this one degrades to a different implementation of the same column.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class FileStreamDegradeTests
{
    private IDbConnection _connection;

    // Runs on the suite's shared kindled database, isolated by a fixture-owned schema; the product name is
    // unique too, because TableQuench drops tables its product no longer declares.
    private readonly string _p = "fsd_" + Guid.NewGuid().ToString("N")[..8];
    private string ProductName => "FsDegradeTest_" + _p;

    private string TableJson => $$"""
        [{
            "Schema": "[{{_p}}]",
            "Name": "[FsWanted]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[G]", "DataType": "UNIQUEIDENTIFIER ROWGUIDCOL", "Nullable": false, "Default": "NEWID()" },
                { "Name": "[Doc]", "DataType": "VARBINARY(MAX)", "Nullable": true, "FileStream": true }
            ],
            "Indexes": [
                { "Name": "[PK_FsWanted]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true },
                { "Name": "[UQ_FsWanted_G]", "IndexColumns": "[G]", "UniqueConstraint": true, "Unique": true }
            ]
        }]
        """;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
            TestSchemaDrop.Drop(cmd, _p);
        Exec($"CREATE SCHEMA [{_p}]");

        // Guards the premise. On a Windows host with FILESTREAM enabled and a FILESTREAM filegroup, the
        // column would deploy for real and every assertion below would be testing nothing.
        Assert.That(Scalar("SELECT CONVERT(INT, ISNULL(SERVERPROPERTY('FilestreamEffectiveLevel'), 0)) "
                           + "+ (SELECT COUNT(*) FROM sys.filegroups WHERE [type] = 'FD')"),
            Is.Zero, "this fixture needs a target with NO FILESTREAM support, or it proves nothing");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandTimeout = 300;
            TestSchemaDrop.Drop(cmd, _p);
        }
        catch (DbException) { /* best-effort cleanup must not mask a test result */ }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
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
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{ProductName}', "
                          + $"@TableDefinitions = N'{json.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void AFileStreamColumn_WithoutServerSupport_DeploysAsPlainVarbinary_AndIsRecordedAsDowngraded()
    {
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%{_p}%'");

        Deploy(TableJson);

        Assert.Multiple(() =>
        {
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('{_p}.FsWanted') "
                               + "AND name = 'Doc'"), Is.EqualTo(1),
                "the column must still exist -- FILESTREAM is a storage choice, and degrading it must not "
                + "cost the user the column itself");

            Assert.That(Scalar("SELECT CONVERT(INT, is_filestream) FROM sys.columns "
                               + $"WHERE [object_id] = OBJECT_ID('{_p}.FsWanted') AND name = 'Doc'"),
                Is.Zero, "and it is genuinely not a FILESTREAM column here");

            Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                               + $"WHERE ActionType = 'downgraded' AND ObjectName LIKE '%{_p}%FsWanted%Doc%'"),
                Is.EqualTo(1),
                "the storage change has to be reported -- a package that asked for FILESTREAM and silently "
                + "got in-row storage is the same class of silent no-op the CDC fix removed");
        });
    }

    [Test]
    public void TheRowGuidColDeclaration_StillApplies_WhenFileStreamIsDegraded()
    {
        // ROWGUIDCOL is independently useful (merge replication), so degrading FILESTREAM must not take
        // it down too. Without this, dropping the whole column definition would pass the test above.
        Deploy(TableJson);

        Assert.That(Scalar("SELECT CONVERT(INT, is_rowguidcol) FROM sys.columns "
                           + $"WHERE [object_id] = OBJECT_ID('{_p}.FsWanted') AND name = 'G'"),
            Is.EqualTo(1),
            "ROWGUIDCOL is declared as part of the DataType and is independently useful (merge "
            + "replication), so degrading FILESTREAM must not take it down too");
    }

    [Test]
    public void ATableWithNoFileStreamColumns_IsNotDowngraded()
    {
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%{_p}%'");

        Deploy($$"""
            [{
                "Schema": "[{{_p}}]",
                "Name": "[NoFsWanted]",
                "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "[PK_NoFsWanted]", "IndexColumns": "[Id]", "PrimaryKey": true, "Unique": true } ]
            }]
            """);

        Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit "
                           + $"WHERE ActionType = 'downgraded' AND ObjectName LIKE '%{_p}%NoFsWanted%'"),
            Is.Zero, "a table that never asked for FILESTREAM must not be reported as downgraded");
    }
}
